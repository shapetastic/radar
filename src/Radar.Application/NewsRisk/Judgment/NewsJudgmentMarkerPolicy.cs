using System.Globalization;
using System.Text;

using Radar.Application.NewsTyping;
using Radar.Application.Reporting;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The ONE derivation of a leader row's semantic-read state from a judgment record (spec 185 §4). Pure,
/// static and TOTAL: every input — including a null record and a mismatched run — maps to exactly one of
/// the three states, so the model never chooses presentation and an absent marker is unrepresentable.
/// <list type="bullet">
/// <item><b>⚠ challenged</b>: status <see cref="NewsJudgmentStatus.Judged"/> with ≥ 1 surviving finding;
/// the summary names the top finding (severity descending, then confidence descending — deterministic);</item>
/// <item><b>· no challenge found in supplied facts</b>: ONLY status <c>Judged</c> with zero findings AND a
/// trajectory that is not <see cref="NewsJudgmentTrajectory.Deteriorating"/> — a completed, validated
/// judgment; carries the typing-incomplete qualifier whenever the record's typing completeness is not
/// <see cref="NewsTypingCompleteness.Complete"/>;</item>
/// <item><b>? unassessed (reason)</b>: everything else, with the closed reason-token vocabulary — a
/// judgment from a PRIOR run is <c>stale</c> (no stale carryover ever qualifies a row), and a spec-187 §1
/// <see cref="NewsJudgmentStatus.AttemptsExhausted"/> record is <c>retries-exhausted</c>.</item>
/// </list>
/// <para>
/// Spec 186 §1 — trajectory honesty, all of it here in the pure policy and never in the validator (a
/// zero-findings <c>Deteriorating</c> read is a legitimate model output: the spec-179 challenge taxonomy
/// has no bucket for gradual decline, which is exactly why the trajectory axis exists):
/// <list type="bullet">
/// <item><b>Deteriorating never renders the dot.</b> <c>Judged</c> + zero findings + <c>Deteriorating</c>
/// is <c>Challenged</c> with the deterministic summary <c>business-trajectory-deteriorating</c>. No finding
/// is invented — the summary names the trajectory AXIS, and its provenance is the persisted record;</item>
/// <item><b>every judged marker carries the trajectory token</b>, uniformly across both judged states, so
/// the display is state-complete and the dot can never silently imply health;</item>
/// <item><b>a null persisted trajectory under <c>Judged</c> is INVALID, not unknown</b> — the validator
/// requires the token to parse, so it can only be a corrupted/hand-edited record: it renders
/// <c>? unassessed (invalid-record)</c>. The genuine <c>Unknown</c> ENUM value stays a valid completed read
/// and keeps the dot plus its <c>unknown</c> token;</item>
/// <item>every same-run record-derived marker carries its <c>JudgmentId</c>, so the report's judgment
/// provenance appendix can make the traceability claim TRUE rather than assert it.</item>
/// </list>
/// </para>
/// <para>
/// Spec 214 §4 — a judged marker with a DIRECTIONAL trajectory also carries its
/// <see cref="NewsJudgmentLeaderMarker.TrajectoryBasis"/> token (<see cref="TrajectoryBasisToken"/>):
/// <c>Supported</c>, <c>LevelOnly</c>, <c>ReferenceSupported</c> (spec 215), or <c>(pre-214)</c> when the
/// record predates the basis. Mixed and Unknown carry none (the basis is not applicable), so the appendix
/// never states a basis for a non-direction. Spec 215 §4 adds
/// <see cref="NewsJudgmentLeaderMarker.ReferenceIds"/> (<see cref="ReferenceIdsToken"/>): the cited
/// trajectory reference ids, comma-joined, on a judged marker that cited any; null otherwise.
/// </para>
/// </summary>
public static class NewsJudgmentMarkerPolicy
{
    public static NewsJudgmentLeaderMarker Derive(NewsJudgmentRecord? record, Guid? currentRunId)
    {
        if (record is null)
        {
            return new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.NotACandidate);
        }

        if (record.RunId != currentRunId)
        {
            // Only same-run judgments qualify a row (spec 185 §4): a prior run's verdict is stale.
            return new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.Stale);
        }

        return record.Status switch
        {
            // A completed judgment with no trajectory cannot come from the validator — it is a corrupted
            // record, and a corrupted record never earns a dot (spec 186 §1).
            NewsJudgmentStatus.Judged when record.BusinessTrajectory is null => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed,
                NewsJudgmentMarkerReasons.InvalidRecord,
                JudgmentId: record.JudgmentId),
            NewsJudgmentStatus.Judged when record.Findings.Count > 0 => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Challenged,
                ChallengeSummary: TopFindingSummary(record.Findings),
                Trajectory: TrajectoryToken(record.BusinessTrajectory.Value),
                JudgmentId: record.JudgmentId,
                TrajectoryBasis: TrajectoryBasisToken(record),
                ReferenceIds: ReferenceIdsToken(record)),
            // Zero findings but a deteriorating factual trajectory: an ABSENCE claim would be rendered
            // beside the same record's contrary PRESENCE evidence. The axis is the challenge.
            NewsJudgmentStatus.Judged
                when record.BusinessTrajectory == NewsJudgmentTrajectory.Deteriorating =>
                new NewsJudgmentLeaderMarker(
                    NewsJudgmentMarkerState.Challenged,
                    ChallengeSummary: DeterioratingTrajectorySummary,
                    Trajectory: TrajectoryToken(NewsJudgmentTrajectory.Deteriorating),
                    JudgmentId: record.JudgmentId,
                    TrajectoryBasis: TrajectoryBasisToken(record),
                    ReferenceIds: ReferenceIdsToken(record)),
            NewsJudgmentStatus.Judged => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.NoChallengeFound,
                TypingIncomplete: record.TypingCompleteness != NewsTypingCompleteness.Complete,
                Trajectory: TrajectoryToken(record.BusinessTrajectory.Value),
                JudgmentId: record.JudgmentId,
                TrajectoryBasis: TrajectoryBasisToken(record),
                ReferenceIds: ReferenceIdsToken(record)),
            NewsJudgmentStatus.InsufficientFacts => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed,
                NewsJudgmentMarkerReasons.InsufficientFacts,
                JudgmentId: record.JudgmentId),
            NewsJudgmentStatus.ProviderFailure => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed,
                NewsJudgmentMarkerReasons.ProviderFailure,
                JudgmentId: record.JudgmentId),
            NewsJudgmentStatus.ParseFailure => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed,
                NewsJudgmentMarkerReasons.ParseFailure,
                JudgmentId: record.JudgmentId),
            // Spec 187 §1: the attempt bound was reached, so NO call was made this run. Unassessed with a
            // named reason — never a dot (nothing was assessed) and never a challenge (nothing was found).
            NewsJudgmentStatus.AttemptsExhausted => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed,
                NewsJudgmentMarkerReasons.RetriesExhausted,
                JudgmentId: record.JudgmentId),
            _ => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed,
                NewsJudgmentMarkerReasons.ValidationFailed,
                JudgmentId: record.JudgmentId),
        };
    }

    /// <summary>
    /// The deterministic summary for a zero-findings deteriorating read (spec 186 §1) — composed FROM the
    /// enum member through <see cref="KebabToken"/>, so the token and the vocabulary cannot drift apart.
    /// </summary>
    internal static readonly string DeterioratingTrajectorySummary =
        "business-trajectory-" + KebabToken(nameof(NewsJudgmentTrajectory.Deteriorating));

    /// <summary>The factual trajectory display token: <c>improving</c> / <c>deteriorating</c> / <c>mixed</c> / <c>unknown</c>.</summary>
    internal static string TrajectoryToken(NewsJudgmentTrajectory trajectory) =>
        KebabToken(trajectory.ToString());

    /// <summary>The display token for a record written before spec 214, whose basis was never computed.</summary>
    internal const string PreSpec214BasisToken = "(pre-214)";

    /// <summary>
    /// Spec 214 §4 — the trajectory-basis display token for a JUDGED record: the enum name
    /// (<c>Supported</c> / <c>LevelOnly</c>) when recorded, <see cref="PreSpec214BasisToken"/> when a
    /// directional record carries none, and <c>null</c> (no token at all) for a Mixed/Unknown/absent
    /// trajectory, where a basis is not applicable and must not be invented.
    /// </summary>
    internal static string? TrajectoryBasisToken(NewsJudgmentRecord record)
    {
        if (record.BusinessTrajectory is not (NewsJudgmentTrajectory.Improving or NewsJudgmentTrajectory.Deteriorating))
        {
            return null;
        }

        return record.TrajectoryBasis is { } basis ? basis.ToString() : PreSpec214BasisToken;
    }

    /// <summary>
    /// Spec 215 §4 — the cited trajectory reference ids for a JUDGED DIRECTIONAL record, comma-joined in
    /// record order (<c>D</c> format), or <c>null</c> when the record cited none, predates the field, or has
    /// no basis to render beside (a Mixed/Unknown read — the validator forbids a cited reference there in
    /// practice, and the appendix must never state references for a non-direction). Rendered on the
    /// judgment provenance appendix beside the basis, never in the leaders cell.
    /// </summary>
    internal static string? ReferenceIdsToken(NewsJudgmentRecord record) =>
        TrajectoryBasisToken(record) is not null && record.TrajectoryReferenceIds is { Count: > 0 } ids
            ? string.Join(",", ids.Select(id => id.ToString("D")))
            : null;

    /// <summary>The top finding's compact summary: severity descending, then confidence descending, then category (AD-3).</summary>
    private static string TopFindingSummary(IReadOnlyList<NewsJudgmentValidatedFinding> findings)
    {
        var top = findings
            .OrderByDescending(f => (int)f.Severity)
            .ThenByDescending(f => f.Confidence)
            .ThenBy(f => (int)f.Category)
            .First();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{KebabToken(top.Category.ToString())}, {KebabToken(top.Severity.ToString())}");
    }

    /// <summary>PascalCase enum name → kebab-case display token (<c>RegulatoryOrLegalSetback</c> → <c>regulatory-or-legal-setback</c>).</summary>
    internal static string KebabToken(string enumName)
    {
        var sb = new StringBuilder(enumName.Length + 4);
        for (var i = 0; i < enumName.Length; i++)
        {
            var c = enumName[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('-');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
