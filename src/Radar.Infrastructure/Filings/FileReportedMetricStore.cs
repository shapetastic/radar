using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Application.Storage;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// The on-disk reported-metrics ledger (spec 215 §1): one JSON file per (company, accession) at
/// <c>{RootDirectory}/{companyId:D}/{sanitizedAccession}.json</c> holding that release's verified
/// <see cref="ReportedMetricRecord"/> LIST. Append-only by construction — <see cref="FileMode.CreateNew"/>
/// makes never-overwrite structural rather than a check-then-write (the spec-206 raw-evidence pattern), so
/// an existing file is <see cref="DurableWriteOutcome.AlreadyAvailable"/> and a re-read of the same
/// accession is a durable no-op. A disk failure is <see cref="DurableWriteOutcome.Failed"/> and never
/// throws; only caller cancellation propagates. Reuses <see cref="RadarFileStoreJson.Options"/> (the shared
/// on-disk JSON shape) and the shared filename-key sanitizer (reuse over copy); all file I/O stays in
/// Infrastructure (AD-5).
/// <para>
/// Read side: <see cref="GetForCompanyAsync"/> enumerates the company folder, deserializes every file, skips
/// an unreadable one with a Warning (never a throw), and returns the flattened records in the deterministic
/// order the seam declares. It is consumed by the stage-2 judge (reference values) and the weekly report
/// (the evidence line's <c>— reported:</c> clause) — never by scoring.
/// </para>
/// </summary>
public sealed class FileReportedMetricStore : IReportedMetricStore
{
    private readonly FileReportedMetricStoreOptions _options;
    private readonly ILogger<FileReportedMetricStore> _logger;

    public FileReportedMetricStore(
        FileReportedMetricStoreOptions options,
        ILogger<FileReportedMetricStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<DurableWriteResult> WriteIfNewAsync(
        Guid companyId, string accession, IReadOnlyList<ReportedMetricRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);

        var companyDirectory = Path.Combine(_options.RootDirectory, companyId.ToString("D"));

        // The shared filename-key sanitizer (FileTickerKey) — despite the ticker-oriented name it is the
        // one filename-safe key helper, used by FileAnalyzedFilingCache and FileFilingReadDebugStore for
        // exactly this accession (reuse over copy). A blank/invalid accession cannot name a file: reported
        // as NotPersisted against the company folder so the failure is diagnosable, never thrown.
        var sanitized = FileTickerKey.Sanitize(accession);
        if (sanitized is null)
        {
            _logger.LogWarning(
                "Reported-metrics accession '{Accession}' for company {CompanyId} is blank or contains invalid "
                    + "filename characters; the ledger file cannot be written.",
                accession,
                companyId);
            return DurableWriteResult.NotPersisted(companyDirectory);
        }

        var path = Path.Combine(companyDirectory, sanitized + ".json");
        if (File.Exists(path))
        {
            // Insert-if-new: the ledger for this accession is already durable. Nothing is written THIS call,
            // and the outcome says so (spec 202 §1's distinction), while Written still reports true.
            return DurableWriteResult.AlreadyOnDisk(path);
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(records, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(
                ex,
                "Failed to serialize {Count} reported-metric record(s) for accession {Accession}; skipping write to {Path}.",
                records.Count,
                accession,
                path);
            return DurableWriteResult.NotPersisted(path);
        }

        try
        {
            Directory.CreateDirectory(companyDirectory);

            // FileMode.CreateNew throws if the file already exists, so even under a race two writers can
            // never overwrite the same append-only ledger file.
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(path, streamOptions))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Wrote {Count} reported-metric record(s) for accession {Accession} (company {CompanyId}) to {Path}.",
                records.Count,
                accession,
                companyId,
                path);
            return DurableWriteResult.Succeeded(path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Insert race: a concurrent writer created the same ledger file first. It is durable either way.
            return DurableWriteResult.AlreadyOnDisk(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk hiccup must never crash the run; the caller counts the failure on its own axis and
            // reports it in ONE aggregated line, so this stays a Warning naming the attempted path.
            _logger.LogWarning(
                ex,
                "Failed to write the reported-metrics ledger for accession {Accession} (company {CompanyId}) at {Path}.",
                accession,
                companyId,
                path);
            return DurableWriteResult.NotPersisted(path);
        }
    }

    public async Task<IReadOnlyList<ReportedMetricRecord>> GetForCompanyAsync(Guid companyId, CancellationToken ct)
    {
        var companyDirectory = Path.Combine(_options.RootDirectory, companyId.ToString("D"));
        if (!Directory.Exists(companyDirectory))
        {
            return [];
        }

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(companyDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Failed to enumerate the reported-metrics ledger under '{Directory}'.", companyDirectory);
            return [];
        }

        var records = new List<ReportedMetricRecord>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<List<ReportedMetricRecord>>(text, RadarFileStoreJson.Options);
                if (parsed is null)
                {
                    _logger.LogWarning("Reported-metrics ledger file '{File}' deserialized to null; skipping.", file);
                    continue;
                }

                records.AddRange(parsed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // One unreadable ledger file must not break a judgment or a report; it is named and skipped.
                _logger.LogWarning(ex, "Failed to read reported-metrics ledger file '{File}'; skipping.", file);
            }
        }

        return
        [
            .. records
                .OrderByDescending(r => r.FilingDateUtc)
                .ThenBy(r => r.Metric)
                .ThenBy(r => r.Period, StringComparer.Ordinal)
                .ThenBy(r => r.Id),
        ];
    }
}
