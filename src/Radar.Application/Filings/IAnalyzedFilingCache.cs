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
/// WHY an analyzed filing produced no DIRECTIONAL signal (spec 204). Before this existed, a confident
/// "materially two-sided quarter" and "the model could not read it" were indistinguishable on disk — one
/// cached token with no direction, confidence, rationale or cause, half of every analysed filing discarded
/// uncounted (224 no-signal vs 222 produced, measured 2026-08-30).
/// <para>
/// Persisted TOKEN-BASED only: <c>RadarFileStoreJson</c> serializes enums through
/// <c>JsonStringEnumConverter(allowIntegerValues: false)</c>, so an integer on disk is REJECTED on read
/// (the whole record then degrades to a cache miss) and the member ordinals below can never leak into a
/// file. <see cref="Unknown"/> is deliberately the zero value (the spec-186 house rule: the degraded
/// default must never be the member that carries the most meaning) — <c>default</c> of this enum replays a
/// NEUTRAL signal, never a Mixed direction.
/// </para>
/// </summary>
public enum FilingNoSignalCause
{
    /// <summary>The model returned <c>Unknown</c> (any confidence): no direction could be established. The safe zero.</summary>
    Unknown = 0,

    /// <summary>The model returned a confident <c>Mixed</c> read: the quarter is materially two-sided.</summary>
    Mixed = 1,

    /// <summary>The (comparability-capped, spec 160) confidence fell below <c>MinConfidence</c>.</summary>
    BelowConfidence = 2,

    /// <summary>
    /// The fetched EX-99.1 body was empty/implausibly short (spec 114). Part of the cause VOCABULARY for
    /// completeness, but never actually persisted: an empty-body read is non-authoritative and is never
    /// cached, so no record on disk may legitimately carry this value.
    /// </summary>
    EmptyBody = 3,
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
/// <para>
/// <see cref="ComparabilityPolicy"/> + <see cref="ComparabilityMarkers"/> (spec 160, trailing + nullable) record
/// the deterministic comparability-scan POLICY the entry was produced under (canonically
/// <c>"cmpscan-v1;cap=&lt;G29&gt;"</c>) and what the scan matched. <c>null</c> policy = written pre-160 ("not
/// scanned" — never a false claim of a clean scan); empty marker lists under a non-null policy = scanned CLEAN.
/// The lookup rule lives in <c>DirectionalFilingSignalSource</c>, not here: a null-policy record is a HIT (heal
/// forward — the accrued cache is never mass-invalidated), while a non-null policy that differs from the current
/// policy string is a MISS (re-analyzed under the current policy, bounded like any miss). Deliberately NOT a
/// <see cref="CurrentCacheVersion"/> bump — the null-policy hit rule IS the migration story.
/// </para>
/// <param name="Accession">The dashed SEC accession this result was analyzed from (the cache key).</param>
/// <param name="Outcome">Whether a directional signal was produced or the read confirmed no directional signal.</param>
/// <param name="Signal">The replayable signal when <see cref="AnalyzedFilingOutcome.DirectionalSignalProduced"/>; else null.</param>
/// <param name="ObservedAtUtc">The observed filing date captured at first analysis (UTC); null when no signal.</param>
/// <param name="CacheVersion">The cache-schema version this entry was written under; a mismatch with
/// <see cref="CurrentCacheVersion"/> is a miss (absent in legacy JSON → 0 → auto-invalidated).</param>
/// <param name="ComparabilityPolicy">The comparability-scan policy string this entry was produced under (spec
/// 160); null = written pre-160, not scanned.</param>
/// <param name="ComparabilityMarkers">What the comparability scan matched (both groups); null = not scanned.</param>
/// <param name="NoSignalCause">SPEC 204 (trailing + nullable): WHY a <see cref="AnalyzedFilingOutcome.NoDirectionalSignal"/>
/// entry produced no direction. <c>null</c> = NOT RECORDED (a pre-204 record, or a produced-signal record where
/// the cause is inapplicable) — never a fabricated cause. A non-null cause is what lets pass-1 replay re-emit
/// the spec-204 read signal deterministically without re-fetching or re-analyzing.</param>
/// <param name="ReadDirection">SPEC 204 (trailing + nullable): the model's OWN direction token
/// (<c>Mixed</c>/<c>Unknown</c>/<c>Improving</c>/<c>Deteriorating</c>) for a no-signal read; <c>null</c> = not recorded.</param>
/// <param name="ReadConfidence">SPEC 204 (trailing + nullable): the EFFECTIVE (comparability-capped, spec 160)
/// read confidence the gate saw — the same value the read signal's metadata and Reason prefix carry, so the
/// record and the signal it replays cannot disagree; <c>null</c> = not recorded.</param>
/// <param name="Rationale">SPEC 204 (trailing + nullable): the model's advice-scrubbed rationale for a
/// no-signal read (empty for an Unknown read that produced none); <c>null</c> = not recorded.</param>
public sealed record AnalyzedFilingRecord(
    string Accession,
    AnalyzedFilingOutcome Outcome,
    ExtractedSignal? Signal,
    DateTimeOffset? ObservedAtUtc,
    int CacheVersion,
    string? ComparabilityPolicy = null,
    ComparabilityMarkers? ComparabilityMarkers = null,
    FilingNoSignalCause? NoSignalCause = null,
    string? ReadDirection = null,
    decimal? ReadConfidence = null,
    string? Rationale = null)
{
    /// <summary>
    /// The current cache-schema version stamped on every write. Deliberately non-zero so a legacy file with no
    /// <c>cacheVersion</c> property (deserializes to 0) is always a mismatch. See the record docs for when to bump.
    /// Bumped 1 → 2 by spec 116: the analyzer system prompt changed materially (profitability/margin-aware
    /// earnings read), so every read cached under the old prompt must be retired and re-analyzed.
    /// Bumped 2 → 3 by spec 204: a no-signal record now names its cause (direction/confidence/rationale), and a
    /// v2 <see cref="AnalyzedFilingOutcome.NoDirectionalSignal"/> record must be re-analyzed so those facts get
    /// recorded. The invalidation is OUTCOME-SCOPED in <c>FileAnalyzedFilingCache.TryGetAsync</c> — a v2
    /// <see cref="AnalyzedFilingOutcome.DirectionalSignalProduced"/> record stays a HIT, because its signal is
    /// intact and re-reading it would spend hosted calls to reproduce a known answer.
    /// </summary>
    public const int CurrentCacheVersion = 3;
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
