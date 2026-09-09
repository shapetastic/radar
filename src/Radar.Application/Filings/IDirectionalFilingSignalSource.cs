using Radar.Application.SignalExtraction;
using Radar.Domain.Evidence;

namespace Radar.Application.Filings;

/// <summary>
/// Opt-in enrichment: for in-scoring-window earnings-8-K Filing evidence, fetch the EX-99.1 body,
/// analyze its directional sentiment, and emit at most one confidence-gated directional GuidanceChange
/// <see cref="ExtractedSignal"/> per filing (Improving -&gt; Positive, Deteriorating -&gt; Negative;
/// Mixed/Unknown/low-confidence -&gt; none). Returns each <see cref="ExtractedSignal"/> paired with the
/// source <see cref="EvidenceItem"/> so the runner threads them through the SAME map -&gt; resolve -&gt;
/// review -&gt; store path as keyword signals (provenance preserved). Every reader/analyzer failure
/// degrades to "no directional signal for that filing" and NEVER aborts the run; only caller
/// cancellation propagates. When AI is disabled this service is not registered and the step is skipped
/// entirely.
/// </summary>
public interface IDirectionalFilingSignalSource
{
    Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
        IReadOnlyList<EvidenceItem> candidateEvidence,
        DateTimeOffset asOfUtc,
        CancellationToken ct);

    /// <summary>
    /// This AI signal source's canonical contribution to the scoring fingerprint — the value read by
    /// <see cref="Radar.Application.Scoring.SignalSourceDescriptor"/> exactly as it reads
    /// <see cref="Radar.Application.Collectors.IEvidenceCollector.CollectorName"/>. It is a deterministic
    /// (AD-3), delimiter-free-or-escaped string encoding the enrichment's <b>per-signal magnitudes</b> that set
    /// an emitted directional <c>GuidanceChange</c> signal's Strength/Novelty/confidence-gate — so enabling the
    /// AI path (vs. disabling it) and tuning those magnitudes both re-stamp <c>ScoringConfigVersion</c>
    /// automatically (restoring AD-10 comparability). Cost/operational caps (per-run fetch limits, rate-limit
    /// breakers) are deliberately excluded, on their own reasoning: they bound how MANY filings are read in a
    /// run, never the Strength, Novelty, direction or confidence gate of any signal that IS emitted, so two
    /// runs at different caps remain comparable. (This exclusion used to be justified by analogy to
    /// <c>ScoringWindowDays</c>; spec 148 reversed that one — the scoring window IS a fingerprint input now,
    /// because it changes WHICH signals are scored. The analogy is gone; the reasoning above stands alone.)
    /// </summary>
    string ScoringDescriptor();
}

/// <summary>
/// An extracted directional filing signal paired with its source evidence (provenance). Since spec 215 it
/// also carries what a FRESH analysis extracted for the reported-metrics ledger:
/// <see cref="ReportedMetrics"/> is <c>null</c> for a cache replay or an extraction-disabled read ("not
/// extracted this pass" — never an empty list meaning "none"), so the collection pass writes the ledger
/// only when there is a genuine extraction to write, with the company id it resolved for the signal.
/// <para>
/// SPEC 216 §2 adds the two fields the pass needs to route and to AUDIT that write, both trailing and
/// nullable: <see cref="Accession"/> (the ledger/outbox key, <c>null</c> only for a hand-built signal in a
/// test) and <see cref="CachedReportedMetricsPolicy"/> — the policy stamp a CACHE REPLAY's record carried.
/// A replay with a stamp but no outbox envelope behind it is the 215-era shape (the extraction was lost
/// before the ledger); it is counted and named rather than acknowledged as an empty ledger.
/// </para>
/// </summary>
public sealed record DirectionalFilingSignal(
    ExtractedSignal Signal,
    EvidenceItem Evidence,
    ReportedMetricExtraction? ReportedMetrics = null,
    string? Accession = null,
    string? CachedReportedMetricsPolicy = null);
