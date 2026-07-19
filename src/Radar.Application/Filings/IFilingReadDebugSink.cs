namespace Radar.Application.Filings;

/// <summary>
/// How a single AI filing-read ATTEMPT resolved (spec 115). Unlike <see cref="AnalyzedFilingOutcome"/> (which
/// collapses everything short of a signal into one replayable no-signal state), this preserves WHY no signal was
/// produced — the exact information that was lost during the 2026-07-18 block-era autopsy, when answering "what
/// did the model actually say?" required busting a cache entry and re-running the pipeline.
/// </summary>
public enum FilingReadOutcome
{
    /// <summary>The read cleared the confidence gate with a directional verdict and produced a GuidanceChange signal.</summary>
    DirectionalSignalProduced,

    /// <summary>The model returned a verdict whose confidence fell below the MinConfidence gate; no signal.</summary>
    BelowConfidence,

    /// <summary>The model returned a non-directional verdict (Mixed/Unknown); no signal regardless of confidence.</summary>
    NoDirectionalRead,

    /// <summary>The fetched EX-99.1 body was empty/implausibly short (spec 114 guard); the model was never called.</summary>
    EmptyBodySkipped,
}

/// <summary>
/// A diagnostic record of one AI filing-read attempt (spec 115): what the read saw (the trimmed EX-99.1 body's
/// length plus a bounded head of it), what the model concluded (direction/confidence/rationale — null for
/// <see cref="FilingReadOutcome.EmptyBodySkipped"/>, where no model call happened), and how the attempt resolved.
/// Diagnostic-only (AD-14 read-side discipline): NEVER an evidence/signal/scoring/report input and never a
/// fingerprint input — it exists solely so the model's actual behaviour is inspectable without re-running the
/// pipeline. <see cref="AsOfUtc"/> is the pipeline's deterministic as-of instant, never wall clock (AD-3).
/// </summary>
/// <param name="Accession">The dashed SEC accession of the analyzed filing.</param>
/// <param name="EvidenceId">The id of the earnings-8-K <c>EvidenceItem</c> the read was attempted for.</param>
/// <param name="InputLength">Length (chars) of the trimmed EX-99.1 body the read saw.</param>
/// <param name="InputHead">A bounded leading slice of that trimmed body (a diagnostic sample, never the scored text).</param>
/// <param name="Direction">The <c>FilingDirection</c> name the model returned; null when no model call happened.</param>
/// <param name="Confidence">The model's confidence in [0,1]; null when no model call happened.</param>
/// <param name="Rationale">The model's (already advice-scrubbed) rationale; null when no model call happened.</param>
/// <param name="Outcome">How the attempt resolved.</param>
/// <param name="AsOfUtc">The pipeline's asOfUtc the attempt ran under (UTC, deterministic — never wall clock).</param>
public sealed record FilingReadDebugRecord(
    string Accession,
    Guid EvidenceId,
    int InputLength,
    string InputHead,
    string? Direction,
    decimal? Confidence,
    string? Rationale,
    FilingReadOutcome Outcome,
    DateTimeOffset AsOfUtc);

/// <summary>
/// Opt-in application seam for persisting AI filing-read diagnostics (spec 115). This is an AD-14 read-side
/// analogue: the sink is consumed by NOTHING in the evidence/signal/scoring/report path — it only records what
/// each read attempt saw and concluded (including no-signal and empty-body outcomes) so a "what did the model
/// actually say?" question never again costs a cache bust and a re-run. Implementations must be best-effort: a
/// write failure is logged and swallowed, never allowed to abort a run or change any produced signal — and the
/// emitting caller additionally guards every call, so even a throwing implementation cannot alter the batch.
/// When the feature is off (the default) no implementation is registered and behaviour is byte-for-byte
/// unchanged.
/// </summary>
public interface IFilingReadDebugSink
{
    /// <summary>Persists <paramref name="record"/> (best-effort; a failure degrades to a logged no-op).</summary>
    Task RecordAsync(FilingReadDebugRecord record, CancellationToken ct);
}
