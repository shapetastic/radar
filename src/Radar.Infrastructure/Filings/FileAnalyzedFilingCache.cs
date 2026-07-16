using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// On-disk per-accession cache of earnings-filing analysis RESULTS (spec 107): one JSON file per accession at
/// <c>{RootDirectory}/{sanitizedAccession}.json</c>. This is an AD-14 analogue — reference/operational data,
/// consumed by nothing in the scoring/evidence/signal/report path: it only lets
/// <see cref="DirectionalFilingSignalSource"/> replay a previously-analyzed filing's
/// <see cref="Radar.Application.SignalExtraction.ExtractedSignal"/> instead of re-fetching the same
/// <c>www.sec.gov/Archives</c> exhibit every run. It reuses the shared
/// <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> + <see cref="RadarFileStoreJson.Options"/> scaffolding
/// (the "reuse over copy" rule) so its on-disk shape and graceful-degrade posture cannot diverge from the other
/// file stores. All file I/O and JSON stay confined to Infrastructure (AD-5).
/// <para>
/// Fail-safe (AD-8): a bad/unreadable/malformed cache file degrades to a cache MISS (logs a warning, returns
/// <c>null</c>) and never crashes a run. A file is NEVER written for a failed read (the caller only calls
/// <see cref="PutAsync"/> on a successful read), so a transient <c>www.sec.gov</c> block cannot poison the cache
/// into skipping a filing forever.
/// </para>
/// </summary>
public sealed class FileAnalyzedFilingCache : IAnalyzedFilingCache
{
    private readonly FileAnalyzedFilingCacheOptions _options;
    private readonly ILogger<FileAnalyzedFilingCache> _logger;

    public FileAnalyzedFilingCache(
        FileAnalyzedFilingCacheOptions options,
        ILogger<FileAnalyzedFilingCache> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<AnalyzedFilingRecord?> TryGetAsync(string accession, CancellationToken ct)
    {
        var path = ResolvePath(accession);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AnalyzedFilingRecord>(text, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // One unreadable/malformed cache file must not break a run — degrade to a cache miss (AD-8). Genuine
            // caller cancellation (OperationCanceledException) is deliberately NOT caught: it propagates.
            _logger.LogWarning(ex, "Failed to read analyzed-filing cache file '{Path}'; treating as a cache miss.", path);
            return null;
        }
    }

    public async Task PutAsync(AnalyzedFilingRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var path = ResolvePath(record.Accession);
        if (path is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(record, RadarFileStoreJson.Options);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Cached analyzed-filing result for accession {Accession} ({Outcome}) to {Path}.",
                record.Accession,
                record.Outcome,
                path);
        }
    }

    private string? ResolvePath(string accession)
    {
        // Reuse the shared filename-key sanitizer (FileTickerKey) — despite the ticker-oriented name it is the
        // shared filename-safe key helper, so we do not paste a second sanitizer (reuse over copy).
        var sanitized = FileTickerKey.Sanitize(accession);
        if (sanitized is null)
        {
            _logger.LogWarning(
                "Analyzed-filing accession '{Accession}' is blank or contains invalid filename characters; skipping cache access.",
                accession);
            return null;
        }

        return Path.Combine(_options.RootDirectory, sanitized + ".json");
    }
}
