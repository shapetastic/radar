namespace Radar.Application.Reporting;

/// <summary>
/// The three semantic-read states (spec 185 §4), derived BY POLICY from the designated presentation
/// cohort's same-run judgment — the model never chooses presentation. The zero value is deliberately the
/// degraded state (the spec-182 convention): anything unresolved reads as unassessed, never as clean.
/// </summary>
public enum NewsJudgmentMarkerState
{
    /// <summary>Judgment unavailable/incomplete for ANY reason — rendered with its reason token.</summary>
    Unassessed = 0,

    /// <summary>At least one validated challenge finding survived; the top finding is summarized.</summary>
    Challenged,

    /// <summary>A completed, validated judgment found no challenge IN THE SUPPLIED FACTS. Never a clean bill.</summary>
    NoChallengeFound,
}

/// <summary>
/// One leader row's semantic-read marker (spec 185 §4): exactly one of the three states, with the reason
/// token for the unassessed state, the top-finding summary for the challenged state, and the
/// typing-incompleteness flag that qualifies the no-challenge wording. Display METADATA only — no score,
/// rank, ordering, label or stored snapshot changes, and deliberately no reference to any judgment type
/// (pure strings/enums, so nothing scoring/pipeline-adjacent can transitively reach the judge).
/// </summary>
public sealed record NewsJudgmentLeaderMarker(
    NewsJudgmentMarkerState State,
    string? UnassessedReason = null,
    string? ChallengeSummary = null,
    bool TypingIncomplete = false)
{
    /// <summary>
    /// The rendered marker cell — a total function over the state, so an absent/blank marker text is
    /// unrepresentable. The no-challenge wording is deliberately narrow ("in supplied facts") and appends
    /// <c>(typing incomplete)</c> whenever typing completeness was not <c>Complete</c>: finding nothing in
    /// facts that were never fully typed is a weaker statement, and it says so.
    /// </summary>
    public string CellText => State switch
    {
        NewsJudgmentMarkerState.Challenged =>
            ChallengeSummary is { Length: > 0 } summary
                ? $"⚠ challenged ({summary})"
                : "⚠ challenged",
        NewsJudgmentMarkerState.NoChallengeFound =>
            TypingIncomplete
                ? "· no challenge found in supplied facts (typing incomplete)"
                : "· no challenge found in supplied facts",
        _ => $"? unassessed ({UnassessedReason ?? NewsJudgmentMarkerReasons.NoJudgment})",
    };
}

/// <summary>The CLOSED reason-token vocabulary for the unassessed state (spec 185 §4).</summary>
public static class NewsJudgmentMarkerReasons
{
    /// <summary>The judgment step is not registered/enabled for this deployment.</summary>
    public const string NoJudgment = "no-judgment";

    /// <summary>The judgment step is registered this run but had not completed when this render happened.</summary>
    public const string JudgmentPending = "judgment-pending";

    public const string InsufficientFacts = "insufficient-facts";
    public const string ProviderFailure = "provider-failure";
    public const string ParseFailure = "parse-failure";
    public const string ValidationFailed = "validation-failed";

    /// <summary>A judgment exists but belongs to a PRIOR run — no stale carryover ever qualifies a row.</summary>
    public const string Stale = "stale";

    /// <summary>The company was not selected under the candidate cost budget this run.</summary>
    public const string NotACandidate = "not-a-candidate";
}

/// <summary>
/// The marker source the weekly report renders from (spec 185 §4), riding <see cref="WeeklyReportModel"/>
/// as a trailing optional (the spec-184 precedent — renderer-facing data never travels on
/// <c>StrategyReportSection</c>, which enters the pipeline result and the news-risk nomination input).
/// States, resolved by <see cref="MarkerCellFor"/> as a TOTAL function so an absent marker is
/// unrepresentable:
/// <list type="bullet">
/// <item>model <c>null</c> — the judgment step is not registered: every row is
/// <c>? unassessed (no-judgment)</c>;</item>
/// <item><see cref="JudgmentPending"/> (markers null) — the step runs AFTER the pipeline's first render:
/// every row is <c>? unassessed (judgment-pending)</c> until the post-judgment re-render;</item>
/// <item>markers present — the per-company marker, or <c>? unassessed (not-a-candidate)</c> for a company
/// the candidate budget never selected.</item>
/// </list>
/// </summary>
public sealed record NewsJudgmentMarkerReportModel(
    bool JudgmentPending,
    IReadOnlyDictionary<Guid, NewsJudgmentLeaderMarker>? Markers = null)
{
    /// <summary>The first-render placeholder: the judgment step is registered but has not run yet.</summary>
    public static NewsJudgmentMarkerReportModel Pending { get; } = new(JudgmentPending: true);

    /// <summary>The ONE marker-cell resolution — total over every input, including a null model and a null/missing map entry.</summary>
    public static string MarkerCellFor(NewsJudgmentMarkerReportModel? model, Guid companyId)
    {
        if (model is null)
        {
            return $"? unassessed ({NewsJudgmentMarkerReasons.NoJudgment})";
        }

        if (model.Markers is null)
        {
            return model.JudgmentPending
                ? $"? unassessed ({NewsJudgmentMarkerReasons.JudgmentPending})"
                : $"? unassessed ({NewsJudgmentMarkerReasons.NoJudgment})";
        }

        return model.Markers.TryGetValue(companyId, out var marker)
            ? marker.CellText
            : $"? unassessed ({NewsJudgmentMarkerReasons.NotACandidate})";
    }
}
