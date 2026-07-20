using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Filings;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Filings;

/// <summary>
/// On-disk per-accession cache of earnings-filing analysis RESULTS (spec 107): one JSON file per accession at
/// <c>{RootDirectory}/{sanitizedAccession}.json</c>, or (spec 118) nested one level deeper under an optional
/// model-identity segment as <c>{RootDirectory}/{ModelSegment}/{sanitizedAccession}.json</c> when
/// <see cref="FileAnalyzedFilingCacheOptions.ModelSegment"/> is set (so a model switch is a clean cache miss).
/// This is an AD-14 analogue — reference/operational data,
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
/// <para>
/// Invalidation (spec 114): every write is stamped with <see cref="AnalyzedFilingRecord.CurrentCacheVersion"/>,
/// and a read whose stored <see cref="AnalyzedFilingRecord.CacheVersion"/> differs is a MISS (the filing is
/// re-analyzed). Legacy files with no <c>cacheVersion</c> property deserialize to 0 and so auto-invalidate —
/// this is how the 2026-07-18 block-era poison self-heals with no manual file deletion.
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
            var record = JsonSerializer.Deserialize<AnalyzedFilingRecord>(text, RadarFileStoreJson.Options);
            if (record is not null && record.CacheVersion != AnalyzedFilingRecord.CurrentCacheVersion)
            {
                // A stale-version entry is not corrupt — it is the deliberate invalidation path (spec 114): the
                // entry predates the current cache schema (legacy files with no cacheVersion property deserialize
                // to 0), so it must be re-analyzed rather than replayed. Debug, not warning: this is expected
                // self-healing, not a fault.
                _logger.LogDebug(
                    "Analyzed-filing cache file '{Path}' has stale CacheVersion {Stale} (current {Current}); treating as a cache miss.",
                    path,
                    record.CacheVersion,
                    AnalyzedFilingRecord.CurrentCacheVersion);
                return null;
            }

            if (record is null || !IsConsistent(record, accession))
            {
                // A malformed-but-parseable record (wrong accession, or an outcome/signal that disagree) is just as
                // untrustworthy as unreadable JSON: returning it as a hit would silently suppress a real signal
                // forever. Degrade to a cache miss so the filing is re-fetched (AD-8).
                _logger.LogWarning(
                    "Analyzed-filing cache file '{Path}' is semantically inconsistent (accession/outcome/signal); treating as a cache miss.",
                    path);
                return null;
            }

            return record;
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

        // Stamp the current cache-schema version on every write (spec 114) so a hit can be trusted to have been
        // produced under the current regime, regardless of what version the caller's record carried.
        if (record.CacheVersion != AnalyzedFilingRecord.CurrentCacheVersion)
        {
            record = record with { CacheVersion = AnalyzedFilingRecord.CurrentCacheVersion };
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

    /// <summary>
    /// Confirms a deserialized record is trustworthy for the accession it was looked up under: the stored
    /// accession must match the requested key, and the outcome and signal must agree (a produced signal must
    /// carry one; a confirmed no-signal must not). An inconsistent record is treated as a miss so a corrupt file
    /// can never permanently suppress a real signal.
    /// </summary>
    private static bool IsConsistent(AnalyzedFilingRecord record, string accession)
    {
        if (!string.Equals(record.Accession, accession, StringComparison.Ordinal))
        {
            return false;
        }

        return record.Outcome switch
        {
            AnalyzedFilingOutcome.DirectionalSignalProduced => record.Signal is not null,
            AnalyzedFilingOutcome.NoDirectionalSignal => record.Signal is null,
            _ => false,
        };
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

        var directory = _options.RootDirectory;
        var segment = _options.ModelSegment;
        if (!string.IsNullOrEmpty(segment))
        {
            // Defense-in-depth: the DI helper (CacheModelSegment) always yields a single filename-safe token, but
            // if ModelSegment is ever constructed elsewhere as a rooted path ("/tmp", "C:\\..."), a nested path, or
            // one containing "..", Path.Combine could escape RootDirectory. Only fold in a segment that is a single
            // safe path component; otherwise ignore it and use the root layout so a cache file can never be written
            // outside the cache root.
            if (IsSafeSegment(segment))
            {
                directory = Path.Combine(directory, segment);
            }
            else
            {
                _logger.LogWarning(
                    "Analyzed-filing cache ModelSegment '{Segment}' is not a single filename-safe path component; "
                        + "ignoring it and using the cache root to stay within RootDirectory.",
                    segment);
            }
        }

        return Path.Combine(directory, sanitized + ".json");
    }

    // A ModelSegment is usable only if it is a single, in-root path component: not rooted, carrying no invalid
    // filename characters (which on every platform includes the directory separators), and not "." / "..".
    private static bool IsSafeSegment(string segment) =>
        !Path.IsPathRooted(segment)
        && segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && segment is not ("." or "..");
}
