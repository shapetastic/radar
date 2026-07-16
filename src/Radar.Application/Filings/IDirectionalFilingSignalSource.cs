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
    /// breakers) are deliberately excluded, mirroring how <c>ScoringWindowDays</c> is not a fingerprint input.
    /// </summary>
    string ScoringDescriptor();
}

/// <summary>An extracted directional filing signal paired with its source evidence (provenance).</summary>
public sealed record DirectionalFilingSignal(ExtractedSignal Signal, EvidenceItem Evidence);
