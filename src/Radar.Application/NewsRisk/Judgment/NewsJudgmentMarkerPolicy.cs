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
/// <item><b>· no challenge found in supplied facts</b>: ONLY status <c>Judged</c> with zero findings — a
/// completed, validated judgment; carries the typing-incomplete qualifier whenever the record's typing
/// completeness is not <see cref="NewsTypingCompleteness.Complete"/>;</item>
/// <item><b>? unassessed (reason)</b>: everything else, with the closed reason-token vocabulary — a
/// judgment from a PRIOR run is <c>stale</c> (no stale carryover ever qualifies a row).</item>
/// </list>
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
            NewsJudgmentStatus.Judged when record.Findings.Count > 0 => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Challenged,
                ChallengeSummary: TopFindingSummary(record.Findings)),
            NewsJudgmentStatus.Judged => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.NoChallengeFound,
                TypingIncomplete: record.TypingCompleteness != NewsTypingCompleteness.Complete),
            NewsJudgmentStatus.InsufficientFacts => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.InsufficientFacts),
            NewsJudgmentStatus.ProviderFailure => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.ProviderFailure),
            NewsJudgmentStatus.ParseFailure => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.ParseFailure),
            _ => new NewsJudgmentLeaderMarker(
                NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.ValidationFailed),
        };
    }

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
