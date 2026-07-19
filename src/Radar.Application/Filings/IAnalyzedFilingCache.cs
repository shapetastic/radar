using Radar.Application.SignalExtraction;

namespace Radar.Application.Filings;

/// <summary>How a once-analyzed earnings 8-K resolved, so a later run can replay it without re-fetching.</summary>
public enum AnalyzedFilingOutcome
{
    /// <summary>The read succeeded but yielded no directional signal (Mixed/Unknown/below-confidence).</summary>
    NoDirectionalSignal,

    /// <summary>The read succeeded and produced a directional <see cref="ExtractedSignal"/> (carried on the record).</summary>
    DirectionalSignalProduced,
}

/// <summary>
/// A cached earnings-filing analysis RESULT, keyed by SEC accession (spec 107). It stores the WHOLE
/// <see cref="ExtractedSignal"/> a successful read produced so a replay is field-identical by construction — the
/// cache only changes WHETHER a <c>www.sec.gov</c> fetch happens, never the signal that is scored.
/// <see cref="ObservedAtUtc"/> is the observed filing date captured at first analysis (provenance/audit; UTC),
/// null when no signal was produced.
/// <para>
/// <see cref="CacheVersion"/> is the invalidation key (spec 114): an entry whose version differs from
/// <see cref="CurrentCacheVersion"/> is treated as a cache MISS and the filing is re-analyzed. Bump
/// <see cref="CurrentCacheVersion"/> whenever previously-cached results must be retired wholesale — e.g. the
/// analyzer/prompt contract changes materially, or a systemic defect is found to have produced untrustworthy
/// entries (the 2026-07-18 www.sec.gov block-era poison that motivated this key). The value starts at 1 —
/// never 0 — because legacy JSON files written before the key existed carry no <c>cacheVersion</c> property and
/// deserialize to 0, so they auto-invalidate on read with no manual file deletion.
/// </para>
/// </summary>
/// <param name="Accession">The dashed SEC accession this result was analyzed from (the cache key).</param>
/// <param name="Outcome">Whether a directional signal was produced or the read confirmed no directional signal.</param>
/// <param name="Signal">The replayable signal when <see cref="AnalyzedFilingOutcome.DirectionalSignalProduced"/>; else null.</param>
/// <param name="ObservedAtUtc">The observed filing date captured at first analysis (UTC); null when no signal.</param>
/// <param name="CacheVersion">The cache-schema version this entry was written under; a mismatch with
/// <see cref="CurrentCacheVersion"/> is a miss (absent in legacy JSON → 0 → auto-invalidated).</param>
public sealed record AnalyzedFilingRecord(
    string Accession,
    AnalyzedFilingOutcome Outcome,
    ExtractedSignal? Signal,
    DateTimeOffset? ObservedAtUtc,
    int CacheVersion)
{
    /// <summary>
    /// The current cache-schema version stamped on every write. Deliberately non-zero so a legacy file with no
    /// <c>cacheVersion</c> property (deserializes to 0) is always a mismatch. See the record docs for when to bump.
    /// </summary>
    public const int CurrentCacheVersion = 1;
}

/// <summary>
/// Application seam for a per-accession earnings-analysis-result cache (spec 107). This is an AD-14 analogue:
/// reference/operational data, NEVER evidence, a signal source, a collector, or a scoring/fingerprint input. It
/// exists only to let <c>DirectionalFilingSignalSource</c> replay a previously-analyzed filing's result instead
/// of re-fetching the same <c>www.sec.gov/Archives</c> exhibit every run — the replayed
/// <see cref="ExtractedSignal"/> is identical to what a fresh read would have produced, so the scored signal set
/// is unchanged. Only successful reads (a signal or a confirmed no-signal) are ever cached; a failed or
/// non-authoritative (empty/implausibly-short body) read is never cached, so a transient block cannot
/// permanently suppress a filing.
/// </summary>
public interface IAnalyzedFilingCache
{
    /// <summary>Returns the cached result for <paramref name="accession"/>, or null on a miss (never throws).</summary>
    Task<AnalyzedFilingRecord?> TryGetAsync(string accession, CancellationToken ct);

    /// <summary>Persists <paramref name="record"/> keyed by its accession (best-effort; a disk failure degrades to a no-op).</summary>
    Task PutAsync(AnalyzedFilingRecord record, CancellationToken ct);
}
