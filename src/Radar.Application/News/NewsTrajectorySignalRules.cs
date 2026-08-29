using Radar.Application.NewsRisk.Judgment;
using Radar.Application.SignalExtraction;
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
/// The magnitudes are the news analogue of the AI filing read's <c>str</c>/<c>nov</c>/<c>minconf</c>; spec
/// 194 §2 folds them into the scoring identity BY VALUE, so a judge-model, presentation-cohort or
/// strength-constant change can no longer hide inside an unchanged <c>ScoringConfigVersion</c>.
/// <b>Spec 201 §3: the magnitudes themselves now live in
/// <see cref="NewsTrajectorySignalConstants"/> (<c>Radar.Application.SignalExtraction</c>)</b>, because
/// <c>Radar.Application.Scoring</c> — which is banned at source level from referencing this namespace —
/// legitimately needs the base strength. The members below are read-through aliases so every existing
/// caller keeps one name for one value; this class remains the MAPPING (it needs the NewsRisk trajectory
/// enum, which is why the mapping could not move with the constants).
/// </para>
/// </summary>
internal static class NewsTrajectorySignalRules
{
    /// <inheritdoc cref="NewsTrajectorySignalConstants.BaseStrength"/>
    internal const int BaseStrength = NewsTrajectorySignalConstants.BaseStrength;

    /// <inheritdoc cref="NewsTrajectorySignalConstants.MaxFindingContribution"/>
    internal const int MaxFindingContribution = NewsTrajectorySignalConstants.MaxFindingContribution;

    /// <inheritdoc cref="NewsTrajectorySignalConstants.CompleteTypingBonus"/>
    internal const int CompleteTypingBonus = NewsTrajectorySignalConstants.CompleteTypingBonus;

    /// <inheritdoc cref="NewsTrajectorySignalConstants.Novelty"/>
    internal const int Novelty = NewsTrajectorySignalConstants.Novelty;

    /// <inheritdoc cref="NewsTrajectorySignalConstants.Confidence"/>
    internal const decimal Confidence = NewsTrajectorySignalConstants.Confidence;

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
