using Radar.Application.NewsRisk.Judgment;
using Radar.Domain.Signals;

namespace Radar.Application.News;

/// <summary>
/// The deterministic mapping from a judged business trajectory to a signal direction and strength.
/// Small, separately testable and visibly constant: <c>Improving → Positive</c>,
/// <c>Deteriorating → Negative</c>, and <c>Mixed</c>/<c>Unknown</c> → no direction at all (genuine
/// both-ways evidence is not a direction, and a judge that declined to call has not called).
/// <para>
/// <b>SPEC 194 — this is no longer the article-INHERITANCE rule.</b> Spec 191 applied it inside
/// <c>KeywordSignalExtractor</c>'s news branch, so a newly collected article took the direction of whatever
/// company-level judgment happened to exist — necessarily one produced from EARLIER articles it had never
/// read. That seam is retired. These rules now belong to the judgment-DERIVED signal: the one signal a
/// validated presentation-cohort judgment materializes for itself, anchored to the evidence that judgment
/// actually cited. The mapping and the magnitudes are unchanged; what changed is which durable signal
/// carries them.
/// </para>
/// <para>
/// The magnitudes below are the news analogue of the AI filing read's <c>str</c>/<c>nov</c>/<c>minconf</c>.
/// Unlike those they are currently hashed into NO fingerprint — spec 194 §2 folds them into the scoring
/// identity, so that a judge-model, presentation-cohort or strength-constant change can no longer hide
/// inside an unchanged <c>ScoringConfigVersion</c>.
/// </para>
/// </summary>
internal static class NewsTrajectorySignalRules
{
    /// <summary>The Neutral <c>MediaAttention</c> strength the extractor has always emitted — the floor a directional read builds on.</summary>
    internal const int BaseStrength = 4;

    /// <summary>The maximum number of judge findings that contribute to strength.</summary>
    internal const int MaxFindingContribution = 3;

    /// <summary>The bonus for a judgment whose stage-1 typing was COMPLETE (nothing deferred, nothing failed).</summary>
    internal const int CompleteTypingBonus = 1;

    /// <summary>
    /// The Novelty a news attention signal carries. Spec 194 §1.2 requires the judgment-derived signal to
    /// retain spec 191's declared values, and 191 declared them by inheriting the ordinary news branch's:
    /// verified against <c>KeywordSignalExtractor</c>'s spec-191 news branch, which set
    /// <c>Novelty: 4, Confidence: 0.5m</c> on BOTH its Neutral and its directional signal.
    /// <para>
    /// Declared here, beside the direction/strength rules, so every magnitude a judgment-derived news
    /// signal carries is readable in one place — and so spec 194 §2 can fold the whole set into the scoring
    /// identity. It deliberately does NOT edit the extractor to read this const: §1.1 restored that branch
    /// to a byte-identical pre-191 form and proving it stays that way is worth more than sharing a literal.
    /// If the ordinary news branch's magnitudes ever move, move these with them.
    /// </para>
    /// </summary>
    internal const int Novelty = 4;

    /// <summary>The Confidence a news attention signal carries — see <see cref="Novelty"/> for provenance.</summary>
    internal const decimal Confidence = 0.5m;

    /// <summary>The direction, or <c>null</c> when the trajectory carries none.</summary>
    internal static SignalDirection? DirectionFor(NewsJudgmentTrajectory trajectory) => trajectory switch
    {
        NewsJudgmentTrajectory.Improving => SignalDirection.Positive,
        NewsJudgmentTrajectory.Deteriorating => SignalDirection.Negative,
        NewsJudgmentTrajectory.Mixed => null,
        NewsJudgmentTrajectory.Unknown => null,
        _ => null,
    };

    /// <summary>
    /// Strength, scaled by the judge's finding count and typing completeness, clamped to the domain range.
    /// Range 4–8: the base is the Neutral media-attention strength, so a directional read is never WEAKER
    /// than the attention event it accompanies.
    /// <para>
    /// A supportive <c>Improving</c> read legitimately carries ZERO findings — spec 185's findings are
    /// CHALLENGE-only, so an improving trajectory has nothing to list — and therefore lands at the base
    /// strength unless typing was complete. That is intended: an unchallenged improving read is a real but
    /// modest input, not a thesis on its own.
    /// </para>
    /// <para>
    /// The completeness bonus is SCORE-RELEVANT, which is why spec 194 §3 scoped a retryable stage-1 typing
    /// failure to the checkpoint window: a falsely non-<c>Complete</c> company would silently lose this
    /// point because some unrelated out-of-window backlog observation failed.
    /// </para>
    /// </summary>
    internal static int StrengthFor(int findingCount, bool typingComplete) => Math.Clamp(
        BaseStrength
            + Math.Min(Math.Max(findingCount, 0), MaxFindingContribution)
            + (typingComplete ? CompleteTypingBonus : 0),
        1,
        10);
}
