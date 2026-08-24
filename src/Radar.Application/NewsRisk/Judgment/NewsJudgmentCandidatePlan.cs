using Radar.Application.Reporting;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The ordered, immutable set of companies THIS run is about to judge (spec 187 §2) — computed EXACTLY
/// ONCE, by the Worker, immediately after the pipeline produced its structured strategy sections, and then
/// handed to BOTH the stage-1 typing pass and the stage-2 judge.
/// <para>
/// <b>Why this type exists.</b> Before spec 187 the judge selected its own candidates from the strategy
/// sections while the typing pass knew nothing about them, so the first live run spent its whole 200-call
/// typing budget on the global 30-day/backlog queue and then judged 18 companies whose motivating headlines
/// were still untyped. Sharing ONE plan makes "the companies typing prioritized" and "the companies the
/// judge assessed" the same list, in the same order, by construction rather than by two implementations
/// happening to agree.
/// </para>
/// <para>
/// <b>Selection policy is NOT duplicated here.</b> The plan is a frozen carrier; the rules live in
/// <see cref="NewsRiskCandidateSelector"/> (the spec-179 §3 traversal), invoked once by
/// <see cref="NewsJudgmentCandidatePlanner"/>.
/// </para>
/// <para>
/// <b>Candidate status changes SELECTION ORDER ONLY.</b> Nothing on this type reaches typing content,
/// validation, cohort identity or fact-family membership — and nothing here is a scoring, ranking or
/// fingerprint input.
/// </para>
/// </summary>
public sealed class NewsJudgmentCandidatePlan
{
    /// <summary>The "no candidate plan" value — judgment disabled, or a run with no strategy sections. Typing then behaves exactly as it did before spec 187 §2.</summary>
    public static readonly NewsJudgmentCandidatePlan Empty = new([]);

    public NewsJudgmentCandidatePlan(IReadOnlyList<NewsRiskCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Copied on construction: the plan is handed to two independent passes and must be incapable of
        // changing between them (a plan that could drift would silently reintroduce the very divergence
        // this type exists to remove).
        Candidates = [.. candidates];
        CompanyIds = [.. Candidates.Select(c => c.CompanyId)];
    }

    /// <summary>The candidates in traversal order — the EXACT list, in the EXACT order, the judge consumes.</summary>
    public IReadOnlyList<NewsRiskCandidate> Candidates { get; }

    /// <summary>
    /// The same list projected to company ids, in the same order: the round-robin key the typing pass's
    /// candidate lane walks. Already distinct — <see cref="NewsRiskCandidateSelector"/> dedupes by company
    /// id while retaining every selecting strategy's provenance on the candidate itself.
    /// </summary>
    public IReadOnlyList<Guid> CompanyIds { get; }

    /// <summary>How many companies this run plans to judge.</summary>
    public int Count => Candidates.Count;
}

/// <summary>
/// The ONE place per run where judgment-candidate selection is invoked (spec 187 §2). Registered WITH the
/// judgment step, so it is absent — and the plan is therefore <c>null</c> — whenever judgment is disabled;
/// the typing pass then keeps its pre-187 §2 global selection exactly.
/// </summary>
public interface INewsJudgmentCandidatePlanner
{
    /// <summary>
    /// Freezes this run's ordered judgment-candidate plan from the report's structured strategy sections.
    /// Absent/empty sections yield <see cref="NewsJudgmentCandidatePlan.Empty"/> — never a fabricated
    /// candidate, and never an exception (a run with no sections is a legitimate state, not an error).
    /// </summary>
    NewsJudgmentCandidatePlan Plan(IReadOnlyList<StrategyReportSection>? sections);
}

/// <summary>
/// Applies the spec-179 §3 <see cref="NewsRiskCandidateSelector"/> traversal at the resolved
/// <see cref="NewsJudgmentOptions.MaxCompaniesPerRun"/> budget — reused, never reimplemented, so the typing
/// priority order and the judged order cannot drift from the single selection policy.
/// </summary>
public sealed class NewsJudgmentCandidatePlanner : INewsJudgmentCandidatePlanner
{
    private readonly NewsJudgmentOptions _options;

    public NewsJudgmentCandidatePlanner(NewsJudgmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public NewsJudgmentCandidatePlan Plan(IReadOnlyList<StrategyReportSection>? sections) =>
        sections is { Count: > 0 }
            ? new NewsJudgmentCandidatePlan(
                NewsRiskCandidateSelector.Select(sections, _options.MaxCompaniesPerRun))
            : NewsJudgmentCandidatePlan.Empty;
}
