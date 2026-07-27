using Radar.Domain.Companies;
using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// The complete, pre-windowed input to a single company score computation. The engine (task 15) is
/// responsible for selecting the window and the signals; the formula is a pure function of this input.
/// Each <see cref="ScoringSignal"/>'s <c>Evidence.Id</c> equals its <c>Signal.EvidenceId</c> — the
/// engine guarantees this and the formula may assume it.
///
/// <para><see cref="Signals"/> is the CURRENT window (start, end] — each paired with its source
/// evidence for provenance. <see cref="PreviousSignals"/> is the immediately-preceding window of the
/// same length (start - window, start], carried as signals ONLY (no evidence): it exists so the
/// formula can measure signal-activity acceleration (velocity). It must NOT be used to build
/// contributions / ScoreEvidenceLinks — only the current-window signals carry provenance.</para>
///
/// <para><see cref="FollowingTier"/> is the company's curated "how followed already" tier from the seed
/// (spec 117) — a NON-PRICE notedness input (AD-14: never derived from price/market cap/volume) the v7
/// Opportunity discount folds in so already-noticed improvement is discounted harder than under-followed
/// improvement. Defaults to <see cref="FollowingTier.Small"/> (no extra discount) — the fail-safe when the
/// company/tier is unknown.</para>
///
/// <para><see cref="PreCollapseSignals"/> is the SAME current window as <see cref="Signals"/> but as it
/// stood BEFORE the engine applied the spec-109 same-event <c>MediaAttentionCollapse</c> (spec 122). The
/// collapse keeps one representative per event, which correctly removes duplicate media VOLUME but also
/// discards the distinct-publisher BREADTH of the outlets it dropped; <c>radar-formula-v8</c> reads this set
/// to credit those collapsed-away publishers back into the Attention breadth term (tier-weighted, scaled by
/// <see cref="ScoringWeights.CollapsedBreadthCredit"/>). It is a BREADTH-only input: it must NOT be used for
/// trajectory, velocity, media counts, or contributions/ScoreEvidenceLinks — provenance still comes from
/// <see cref="Signals"/> alone. Defaults to empty, which reproduces the pre-v8 (post-collapse-only)
/// behaviour — the fail-safe when a caller has no pre-collapse set.</para>
///
/// <para><see cref="EnabledCollectors"/> is the enabled-collector VOCABULARY for this run (spec 146, made
/// mode-independent by spec 147) — the answer to "is that source part of this run's configuration?". It is
/// provenance only: no formula may use it to change a score (a channel scores 0 whether its source was down
/// or genuinely quiet — Radar scores evidence, and absence of evidence is not evidence), and it is hashed
/// into nothing. Defaults to empty, the honest answer when the caller cannot say.
/// <b>Read the "what did not run actually means" note on
/// <see cref="ISignalSourceDescriptor.EnabledCollectors"/> before treating a channel's
/// <c>CollectorsNotRun</c> as an outage signal</b>: the startup guard validates channel collectors against
/// this very list, so in a composed run it is structurally empty.</para>
/// </summary>
public sealed record ScoringInput(
    Guid CompanyId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<ScoringSignal> Signals,
    IReadOnlyList<Signal> PreviousSignals,
    FollowingTier FollowingTier = FollowingTier.Small)
{
    /// <summary>
    /// The current-window signal set BEFORE the spec-109 same-event media collapse (spec 122). Breadth-only;
    /// empty means "no collapse information", which scores exactly as the post-collapse set alone.
    /// </summary>
    public IReadOnlyList<ScoringSignal> PreCollapseSignals { get; init; } = Array.Empty<ScoringSignal>();

    /// <summary>
    /// The <c>IEvidenceCollector.CollectorName</c>s this run is configured with (spec 146; sourced from the
    /// mode-independent <c>EnabledCollectorVocabulary</c> since spec 147), ordered and distinct. Provenance
    /// ONLY — never a scoring input, never hashed. Empty means "unknown", which every consumer must read as
    /// "did not run".
    /// </summary>
    public IReadOnlyList<string> EnabledCollectors { get; init; } = Array.Empty<string>();
}
