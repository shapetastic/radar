using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;

namespace Radar.Application.News;

/// <summary>
/// SPEC 194 §2 — the ONE production composer of <see cref="NewsJudgmentScoringIdentity"/>.
/// <para>
/// <b>Why it is a separate type on this side of the boundary.</b> The identity itself lives in
/// <c>Radar.Application.Scoring</c>, and the spec-177/179 architecture guards forbid any type in that
/// namespace from reaching <c>Radar.Application.News</c> or <c>Radar.Application.NewsRisk</c> — the whole
/// point being that a score must never be able to reach into the news subsystem. The trajectory→direction
/// mapping and the strength constants nonetheless belong to that subsystem
/// (<see cref="NewsTrajectorySignalRules"/>, whose <c>DirectionFor</c> takes a
/// <see cref="NewsJudgmentTrajectory"/>). This factory resolves them HERE and hands the identity nothing but
/// rendered strings and numbers, so the dependency points news → scoring and never the reverse.
/// </para>
/// <para>
/// Reading the constants rather than restating them is the point: a copy would drift, and a drifted copy
/// would mean the stamp claimed magnitudes the materializer does not use — a fingerprint that lies is worse
/// than one that is missing.
/// </para>
/// </summary>
public static class NewsJudgmentScoringIdentityFactory
{
    /// <summary>
    /// The separator between a trajectory and the direction it maps to, inside one mapping token.
    /// Purely presentational within the segment: the whole token is escaped before it is spliced.
    /// </summary>
    private const string MappingArrow = ">";

    /// <summary>The token a trajectory that maps to NO direction renders (<c>Mixed</c>, <c>Unknown</c>).</summary>
    private const string NoDirectionToken = "none";

    /// <summary>
    /// The trajectory→direction mapping, one token per declared <see cref="NewsJudgmentTrajectory"/>, in
    /// ENUM-VALUE ORDER (AD-3: a stable, declaration-independent order). Enumerating the enum rather than
    /// listing the four members means a NEW trajectory member cannot be added without moving the stamp,
    /// which is the correct outcome — a new trajectory is a new mapping.
    /// <para>
    /// Both halves of the arrow are rendered from the enum names, so the encoding cannot disagree with the
    /// mapping it describes: it IS the mapping, evaluated.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> DirectionMappingTokens { get; } =
    [
        .. Enum.GetValues<NewsJudgmentTrajectory>()
            .OrderBy(static trajectory => (int)trajectory)
            .Select(static trajectory =>
                $"{trajectory}{MappingArrow}"
                    + (NewsTrajectorySignalRules.DirectionFor(trajectory)?.ToString() ?? NoDirectionToken)),
    ];

    /// <summary>
    /// The identity of a composition whose stage-2 judgment is ENABLED and designates
    /// <paramref name="presentationCohortKey"/> as its presentation cohort — resolved at CONFIGURATION time,
    /// so a <c>score</c> or <c>replay</c> pass (which registers no judgment step) stamps exactly what a
    /// <c>full</c> pass over the same configuration stamps.
    /// </summary>
    public static NewsJudgmentScoringIdentity ForPresentationCohort(string presentationCohortKey) =>
        NewsJudgmentScoringIdentity.ForPresentationCohort(
            presentationCohortKey,
            NewsDirectionalSignalMetadata.JudgmentSignalVersionValue,
            DirectionMappingTokens,
            NewsTrajectorySignalRules.BaseStrength,
            NewsTrajectorySignalRules.MaxFindingContribution,
            NewsTrajectorySignalRules.CompleteTypingBonus,
            NewsTrajectorySignalRules.Novelty,
            NewsTrajectorySignalRules.Confidence);
}
