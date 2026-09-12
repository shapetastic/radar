using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Application.Storage;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// The on-disk reported-metrics ledger (spec 215 §1): one JSON file per (company, accession, policy) at
/// <c>{RootDirectory}/{companyId:D}/{sanitizedAccession}.{policy}.json</c> holding that release's verified
/// <see cref="ReportedMetricRecord"/> LIST.
/// <para>
/// <b>SPEC 216 §4 — the write is atomic and a fragment is never mistaken for a record.</b> It goes through
/// the shared <see cref="AtomicFileWriter"/>: the content is serialized to a temp file in the SAME
/// directory, flushed, then committed with a no-overwrite rename. The rename IS the commit point, so an
/// I/O failure part-way through writing leaves only a temp file (deleted on the way out) and never a
/// partial record. The v1 shape wrote straight to the final path with <c>FileMode.CreateNew</c> and then
/// reported any <see cref="IOException"/> over an existing file as
/// <see cref="DurableWriteOutcome.AlreadyAvailable"/> — so a half-written file it had just created itself
/// was reported as a concurrent writer's success, and every later run treated the fragment as the record.
/// </para>
/// <para>
/// <b>The two collision cases are now separate.</b> A move that fails because the path already exists is
/// <see cref="DurableWriteOutcome.AlreadyAvailable"/> ONLY when that file READS BACK as a complete record
/// list; an unparseable existing file is <see cref="DurableWriteOutcome.Failed"/> with reason
/// <c>corrupt-existing</c>, logged once per path, and never <c>AlreadyOnDisk</c>. A disk failure is
/// <see cref="DurableWriteOutcome.Failed"/> and never throws; only caller cancellation propagates.
/// </para>
/// <para>
/// <b>SPEC 216 §5 — the POLICY is an explicit argument and part of the file name.</b> A re-analysis under a
/// later policy therefore writes a NEW file beside the old one instead of colliding with it; nothing is
/// ever deleted (append-only), and choosing which policy's files to read — and counting the superseded
/// ones — belongs to the projector, not here.
/// </para>
/// <para>
/// Read side: <see cref="GetForCompanyAsync"/> enumerates the company folder, deserializes every file,
/// skips an unreadable one with a Warning (never a throw), and returns the flattened records in the
/// deterministic order the seam declares. It is consumed by the stage-2 judge (reference values) and the
/// weekly report (the evidence line's <c>— reported:</c> clause) — never by scoring.
/// </para>
/// Reuses <see cref="RadarFileStoreJson.Options"/> (the shared on-disk JSON shape) and the shared
/// filename-key sanitizer (reuse over copy); all file I/O stays in Infrastructure (AD-5).
/// </summary>
public sealed class FileReportedMetricStore : IReportedMetricStore
{
    /// <summary>The named <see cref="DurableWriteOutcome.Failed"/> reason for an unparseable file already at the target path.</summary>
    internal const string CorruptExistingReason = "corrupt-existing";

    private readonly FileReportedMetricStoreOptions _options;
    private readonly ILogger<FileReportedMetricStore> _logger;

    // One Warning per path per process for a corrupt existing file: the ledger is re-attempted every run,
    // and a permanently corrupt file would otherwise reprint its Warning on every pass forever.
    private readonly HashSet<string> _corruptReported = new(StringComparer.OrdinalIgnoreCase);

    public FileReportedMetricStore(
        FileReportedMetricStoreOptions options,
        ILogger<FileReportedMetricStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// The ledger file name for one (accession, policy) pair — the ONE place the layout is composed, so
    /// the write path and any future reader cannot drift. Returns null when the accession cannot name a
    /// file.
    /// </summary>
    internal static string? FileNameFor(string accession, string policy)
    {
        var sanitizedAccession = FileTickerKey.Sanitize(accession);
        var sanitizedPolicy = FileTickerKey.Sanitize(policy);
        return sanitizedAccession is null || sanitizedPolicy is null
            ? null
            : $"{sanitizedAccession}.{sanitizedPolicy}.json";
    }

    public async Task<DurableWriteResult> WriteIfNewAsync(
        Guid companyId,
        string accession,
        string policy,
        IReadOnlyList<ReportedMetricRecord> records,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(policy);

        var companyDirectory = Path.Combine(_options.RootDirectory, companyId.ToString("D"));

        // The shared filename-key sanitizer (FileTickerKey) — despite the ticker-oriented name it is the
        // one filename-safe key helper, used by FileAnalyzedFilingCache and FileFilingReadDebugStore for
        // exactly this accession (reuse over copy). A blank/invalid accession or policy cannot name a file:
        // reported as NotPersisted against the company folder so the failure is diagnosable, never thrown.
        var fileName = FileNameFor(accession, policy);
        if (fileName is null)
        {
            _logger.LogWarning(
                "Reported-metrics accession '{Accession}' / policy '{Policy}' for company {CompanyId} is blank "
                    + "or contains invalid filename characters; the ledger file cannot be written.",
                accession,
                policy,
                companyId);
            return DurableWriteResult.NotPersisted(companyDirectory);
        }

        var path = Path.Combine(companyDirectory, fileName);
        if (File.Exists(path))
        {
            // Insert-if-new: the ledger for this (accession, policy) may already be durable. "May" is the
            // whole spec-216 §4 point — an existing file only counts as the record when it PARSES as one.
            return ExistingFileOutcome(path);
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

        // The SHARED temp-file + no-overwrite-rename writer (reuse over copy — the outbox commits the same
        // way). The move is the commit point, so an I/O failure part way through leaves only a temp file
        // the writer deletes, never a partial ledger record.
        var written = await AtomicFileWriter.WriteNewAsync(path, json, _logger, ct).ConfigureAwait(false);
        switch (written)
        {
            case AtomicWriteOutcome.Committed:
                _logger.LogInformation(
                    "Wrote {Count} reported-metric record(s) for accession {Accession} (company {CompanyId}, "
                        + "policy {Policy}) to {Path}.",
                    records.Count,
                    accession,
                    companyId,
                    policy,
                    path);
                return DurableWriteResult.Succeeded(path);

            case AtomicWriteOutcome.AlreadyExists:
                // Insert race: a concurrent writer committed the same ledger file first. Whether that is a
                // durable RECORD depends on whether it parses — never assumed.
                return ExistingFileOutcome(path);

            default:
                // The writer already logged the attempted path; the caller counts the failure on its own
                // axis and reports it in ONE aggregated line.
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

    /// <summary>
    /// SPEC 216 §4 — an existing file at the target path is <see cref="DurableWriteOutcome.AlreadyAvailable"/>
    /// ONLY when it reads back as a complete record list. Anything else is a FRAGMENT and is reported
    /// <see cref="DurableWriteOutcome.Failed"/> (reason <c>corrupt-existing</c>), logged once per path, so
    /// the caller's outbox retries it and a partial file is never treated as the record.
    /// </summary>
    private DurableWriteResult ExistingFileOutcome(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<List<ReportedMetricRecord>>(text, RadarFileStoreJson.Options);
            if (parsed is not null)
            {
                return DurableWriteResult.AlreadyOnDisk(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ReportCorrupt(path, ex);
            return DurableWriteResult.NotPersisted(path);
        }

        ReportCorrupt(path, null);
        return DurableWriteResult.NotPersisted(path);
    }

    private void ReportCorrupt(string path, Exception? ex)
    {
        lock (_corruptReported)
        {
            if (!_corruptReported.Add(path))
            {
                return;
            }
        }

        _logger.LogWarning(
            ex,
            "Reported-metrics ledger file '{Path}' already exists but does NOT parse as a complete record "
                + "list ({Reason}); the write is reported as NOT PERSISTED rather than as an existing record, "
                + "so nothing downstream treats the fragment as the ledger. Nothing was overwritten or "
                + "deleted (append-only): recovery is a conscious maintainer action.",
            path,
            CorruptExistingReason);
    }

    /// <summary>The subtree under the root that holds outbox envelopes, never ledger files (spec 216 §2).</summary>
    internal const string OutboxSubdirectoryName = "outbox";

    /// <summary>
    /// SPEC 223 §2 — the accrued ledger inventory. Walks every TOP-LEVEL subdirectory of the root EXCEPT
    /// <c>outbox/</c> (envelopes are not ledger entries) and counts the <c>*.json</c> files directly inside
    /// each company folder, parsing each as a record list. An absent root is the MEASURED zero
    /// (<see cref="ReportedMetricLedgerInventory.AbsentRoot"/>); a root that cannot be enumerated is
    /// <see cref="ReportedMetricLedgerInventory.NotRecorded"/> with the reason — never <c>0</c>; a company
    /// folder that cannot be enumerated likewise makes the whole inventory not-recorded, because a partial
    /// count would render as a smaller measured total. An unparseable file is counted in
    /// <c>UnreadableFiles</c>, contributes zero records, and is named in ONE aggregated Warning for the
    /// whole inventory (never one line per file, which would re-fire for the same corrupt file on every
    /// pass); the names are capped with the remainder counted. Never throws for a disk failure;
    /// only caller cancellation propagates.
    /// </summary>
    public async Task<ReportedMetricLedgerInventory> InventoryAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var root = _options.RootDirectory;
        if (!Directory.Exists(root))
        {
            return ReportedMetricLedgerInventory.AbsentRoot;
        }

        List<string> companyDirectories;
        try
        {
            companyDirectories = Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(d => !string.Equals(
                    Path.GetFileName(d), OutboxSubdirectoryName, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Failed to enumerate the reported-metrics ledger root '{Directory}' for its inventory.", root);
            return ReportedMetricLedgerInventory.NotRecorded(
                $"ledger root could not be enumerated: {ex.GetType().Name}");
        }

        var files = 0;
        var records = 0;
        var unreadable = 0;
        var unreadableFiles = new List<string>();
        Exception? lastUnreadableError = null;
        foreach (var companyDirectory in companyDirectories)
        {
            ct.ThrowIfCancellationRequested();

            List<string> ledgerFiles;
            try
            {
                ledgerFiles = Directory
                    .EnumerateFiles(companyDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to enumerate the reported-metrics ledger folder '{Directory}' for its inventory.",
                    companyDirectory);
                return ReportedMetricLedgerInventory.NotRecorded(
                    $"ledger folder '{Path.GetFileName(companyDirectory)}' could not be enumerated: {ex.GetType().Name}");
            }

            foreach (var file in ledgerFiles)
            {
                ct.ThrowIfCancellationRequested();
                files++;
                try
                {
                    // Stream-deserialized, not ReadAllTextAsync: the INVENTORY walks every ledger file on
                    // every pass, so the intermediate string is an allocation per file for no benefit. This
                    // is the convention the newer stores already use (FileAcquisitionStore,
                    // FileBenchmarkUniverseSource) with the same shared RadarFileStoreJson.Options. The
                    // single-record read paths elsewhere in this file are deliberately left alone.
                    List<ReportedMetricRecord>? parsed;
                    await using (var stream = File.OpenRead(file))
                    {
                        parsed = await JsonSerializer
                            .DeserializeAsync<List<ReportedMetricRecord>>(stream, RadarFileStoreJson.Options, ct)
                            .ConfigureAwait(false);
                    }

                    if (parsed is null)
                    {
                        unreadable++;
                        unreadableFiles.Add(file);
                        continue;
                    }

                    records += parsed.Count;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    unreadable++;
                    unreadableFiles.Add(file);
                    lastUnreadableError ??= ex;
                }
            }
        }

        // ONE aggregated line for the whole inventory, never one per file: CLAUDE.md requires an aggregated
        // log line (one per store, never one per item), and a per-file warning would re-fire for the same
        // corrupt file on every pass forever. The files are NAMED so the count is actionable, capped with
        // the remainder counted so a pathological directory cannot flood the log.
        if (unreadableFiles.Count > 0)
        {
            const int NamedLimit = 10;
            var named = string.Join(", ", unreadableFiles.Take(NamedLimit).Select(Path.GetFileName));
            var withheld = unreadableFiles.Count - Math.Min(NamedLimit, unreadableFiles.Count);
            _logger.LogWarning(
                lastUnreadableError,
                "Reported-metrics ledger inventory: {Unreadable} of {Files} ledger file(s) could not be read "
                    + "and contributed no records; named: {NamedFiles}{Withheld}.",
                unreadableFiles.Count,
                files,
                named,
                withheld > 0 ? $" (+{withheld} further not named)" : string.Empty);
        }

        return new ReportedMetricLedgerInventory(
            RootDirectoryExists: true,
            LedgerFiles: files,
            LedgerRecords: records,
            UnreadableFiles: unreadable,
            NotRecordedReason: null);
    }
}
