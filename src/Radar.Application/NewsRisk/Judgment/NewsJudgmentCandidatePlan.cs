using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Reporting;
using Radar.Domain.Companies;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// One planned candidate and the READ DEPTH it will be judged at (spec 219 §2/§3). The pairing lives on the
/// plan rather than being re-derived in the generator, so "how deep was this company read" has exactly one
/// answer per run and the record can state it.
/// </summary>
public sealed record NewsJudgmentPlannedCandidate(
    NewsRiskCandidate Candidate, NewsJudgmentReadDepth Depth);

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
/// <b>Spec 219 §1/§3 — the plan now carries TWO cohorts.</b> <see cref="Candidates"/> is the DEPTH cohort:
/// the unchanged spec-179 §3 traversal (top five evidence-linked rows per Research section), judged at the
/// full <c>MaxFamiliesPerJudgment</c> budget because auditing the names about to be shown to a human is
/// still a valid job. <see cref="BreadthCandidates"/> is everyone else in the company universe, in
/// <see cref="NewsJudgmentCoveragePolicy"/>'s deterministic order, judged at the bounded
/// <c>MaxFamiliesPerBreadthJudgment</c> budget. <see cref="PlannedCandidates"/> is the ONE list the judge
/// walks: depth first in traversal order, then breadth, each entry carrying its depth, deduped by company
/// id so a company in both cohorts appears ONCE — at <see cref="NewsJudgmentReadDepth.Full"/>.
/// </para>
/// <para>
/// <b>Selection policy is NOT duplicated here.</b> The plan is a frozen carrier; the rules live in
/// <see cref="NewsRiskCandidateSelector"/> (the spec-179 §3 traversal) and
/// <see cref="NewsJudgmentCoveragePolicy"/> (the spec-219 §1 breadth pass), both invoked once by
/// <see cref="NewsJudgmentCandidatePlanner"/>.
/// </para>
/// <para>
/// <b>Candidate status changes SELECTION ORDER ONLY.</b> Nothing on this type reaches typing content,
/// validation, cohort identity or fact-family membership — and nothing here is a scoring, ranking or
/// fingerprint input. (The coverage POLICY VERSION is a fingerprint input — see
/// <see cref="NewsJudgmentCoveragePolicy.Version"/> — but that is a config-time constant, never a value
/// this per-run carrier computes.)
/// </para>
/// </summary>
public sealed class NewsJudgmentCandidatePlan
{
    /// <summary>The "no candidate plan" value — judgment disabled, or a run with no strategy sections. Typing then behaves exactly as it did before spec 187 §2.</summary>
    public static readonly NewsJudgmentCandidatePlan Empty = new([]);

    public NewsJudgmentCandidatePlan(
        IReadOnlyList<NewsRiskCandidate> candidates,
        IReadOnlyList<NewsRiskCandidate>? breadthCandidates = null,
        int? companiesInUniverse = null,
        int breadthDroppedByCapacityValve = 0,
        int? capacityValve = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Copied on construction: the plan is handed to two independent passes and must be incapable of
        // changing between them (a plan that could drift would silently reintroduce the very divergence
        // this type exists to remove).
        Candidates = [.. candidates];
        CompanyIds = [.. Candidates.Select(c => c.CompanyId)];

        var depthIds = new HashSet<Guid>(CompanyIds);
        // The dedupe is by COMPANY ID and it resolves in the depth cohort's favour (spec 219 §3): a company
        // the spec-179 traversal selected is judged ONCE, at Full, and the breadth pass must not add a
        // second bounded read of the same company in the same run.
        BreadthCandidates = [.. (breadthCandidates ?? []).Where(c => !depthIds.Contains(c.CompanyId))];
        PlannedCandidates =
        [
            .. Candidates.Select(c => new NewsJudgmentPlannedCandidate(c, NewsJudgmentReadDepth.Full)),
            .. BreadthCandidates.Select(
                c => new NewsJudgmentPlannedCandidate(c, NewsJudgmentReadDepth.Breadth)),
        ];
        CompaniesInUniverse = companiesInUniverse;
        BreadthDroppedByCapacityValve = breadthDroppedByCapacityValve;
        CapacityValve = capacityValve;
    }

    /// <summary>
    /// The DEPTH cohort in traversal order — the spec-179 §3 selection, unchanged in ancestry and unchanged
    /// in order. Judged at the full <c>MaxFamiliesPerJudgment</c> budget.
    /// </summary>
    public IReadOnlyList<NewsRiskCandidate> Candidates { get; }

    /// <summary>
    /// SPEC 219 §1 — the BREADTH cohort: the company universe minus the depth cohort, in
    /// <see cref="NewsJudgmentCoveragePolicy"/>'s order. Empty when judgment planning could not read the
    /// universe (a degraded read is reported as a counted warning by the planner, never as a silent empty).
    /// </summary>
    public IReadOnlyList<NewsRiskCandidate> BreadthCandidates { get; }

    /// <summary>
    /// The ONE ordered list the judge walks: depth first, then breadth, each entry carrying its
    /// <see cref="NewsJudgmentReadDepth"/>. Already distinct by company id.
    /// </summary>
    public IReadOnlyList<NewsJudgmentPlannedCandidate> PlannedCandidates { get; }

    /// <summary>
    /// The DEPTH cohort projected to company ids, in the same order: the round-robin key the typing pass's
    /// candidate lane walks. Already distinct — <see cref="NewsRiskCandidateSelector"/> dedupes by company
    /// id while retaining every selecting strategy's provenance on the candidate itself.
    /// <para>
    /// <b>SPEC 219 DELIBERATELY LEAVES THIS AS THE DEPTH COHORT ONLY.</b> The typing priority lane exists to
    /// stop a leader row being judged from untyped headlines; widening it to the whole universe would spread
    /// one run's typing budget across 102 companies and starve exactly the rows that motivated it. It is
    /// also unnecessary: stage-1 typing already covers all 102 companies (5,500 artifacts, median 44 per
    /// company, minimum 4 — measured 2026-09-09), which is precisely why the breadth read is affordable.
    /// Changing stage-1 typing budgets or the backlog drain is an explicit spec-219 NON-GOAL. Asserted by
    /// test: a plan carrying breadth candidates leaves this list equal to the depth cohort.
    /// </para>
    /// </summary>
    public IReadOnlyList<Guid> CompanyIds { get; }

    /// <summary>How many companies this run plans to judge — both cohorts, after the dedupe.</summary>
    public int Count => PlannedCandidates.Count;

    /// <summary>
    /// SPEC 219 §4 — how many companies the universe held when this plan was frozen. <c>null</c> means NOT
    /// RECORDED (judgment planning never read the repository, or the read failed and was reported), never a
    /// measured zero.
    /// </summary>
    public int? CompaniesInUniverse { get; }

    /// <summary>
    /// SPEC 219 §4 — how many breadth-eligible companies the capacity valve
    /// (<c>MaxCompaniesPerRun</c>) dropped. A MEASURED zero when it did not bite.
    /// </summary>
    public int BreadthDroppedByCapacityValve { get; }

    /// <summary>
    /// The capacity valve in force when this plan was frozen (<c>MaxCompaniesPerRun</c>), so a run that
    /// reports a drop can NAME the cap that bound it. <c>null</c> = not recorded (the
    /// <see cref="Empty"/> plan).
    /// </summary>
    public int? CapacityValve { get; }
}

/// <summary>
/// The ONE place per run where judgment-candidate selection is invoked (spec 187 §2). Registered WITH the
/// judgment step, so it is absent — and the plan is therefore <c>null</c> — whenever judgment is disabled;
/// the typing pass then keeps its pre-187 §2 global selection exactly.
/// </summary>
public interface INewsJudgmentCandidatePlanner
{
    /// <summary>
    /// Freezes this run's ordered judgment-candidate plan: the spec-179 §3 depth traversal over the report's
    /// structured strategy sections, plus (spec 219 §1) the breadth cohort read from the company universe.
    /// Absent/empty sections yield NO depth cohort — never a fabricated candidate, and never an exception (a
    /// run with no sections is a legitimate state, not an error) — while the breadth cohort is still
    /// planned, because universal coverage does not depend on a report existing.
    /// </summary>
    Task<NewsJudgmentCandidatePlan> PlanAsync(
        IReadOnlyList<StrategyReportSection>? sections, CancellationToken ct);
}

/// <summary>
/// Applies the spec-179 §3 <see cref="NewsRiskCandidateSelector"/> traversal at the resolved
/// <see cref="NewsJudgmentOptions.MaxCompaniesPerRun"/> budget — reused, never reimplemented, so the typing
/// priority order and the judged order cannot drift from the single selection policy — and then (spec 219
/// §1) fills the REST of the company universe in as the breadth cohort through
/// <see cref="NewsJudgmentCoveragePolicy"/>.
/// <para>
/// <b>The budget is a safety valve, not a policy</b> (spec 219 §1). It caps the COMBINED cohort: the depth
/// traversal takes what it takes, breadth fills the remainder, and whatever the valve leaves out is COUNTED
/// on the plan and named in the run's aggregated coverage line. It is not a fingerprint input.
/// </para>
/// </summary>
public sealed class NewsJudgmentCandidatePlanner : INewsJudgmentCandidatePlanner
{
    private readonly NewsJudgmentOptions _options;
    private readonly ICompanyRepository _companies;
    private readonly ILogger<NewsJudgmentCandidatePlanner> _logger;

    public NewsJudgmentCandidatePlanner(
        NewsJudgmentOptions options,
        ICompanyRepository companies,
        ILogger<NewsJudgmentCandidatePlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _companies = companies;
        _logger = logger;
    }

    public async Task<NewsJudgmentCandidatePlan> PlanAsync(
        IReadOnlyList<StrategyReportSection>? sections, CancellationToken ct)
    {
        var depth = sections is { Count: > 0 }
            ? NewsRiskCandidateSelector.Select(sections, _options.MaxCompaniesPerRun)
            : [];

        IReadOnlyList<Company> universe;
        try
        {
            universe = await _companies.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A universe read failure degrades to "no breadth candidates" — the depth cohort still runs and
            // the run still produces judgments. It is reported ONCE, as a warning, because a breadth pass
            // that silently covered nobody is indistinguishable from a universe of zero companies, and the
            // whole point of this slice is that coverage gaps are visible.
            _logger.LogWarning(
                ex,
                "News-judgment breadth planning could not read the company universe; this run judges the "
                    + "{Depth} depth candidate(s) ONLY and covers no additional companies. Coverage policy "
                    + "{CoveragePolicy}.",
                depth.Count,
                NewsJudgmentCoveragePolicy.Version);
            return new NewsJudgmentCandidatePlan(
                depth,
                breadthCandidates: [],
                companiesInUniverse: null,
                breadthDroppedByCapacityValve: 0,
                capacityValve: _options.MaxCompaniesPerRun);
        }

        var breadth = NewsJudgmentCoveragePolicy.Select(
            universe,
            new HashSet<Guid>(depth.Select(c => c.CompanyId)),
            remainingCapacity: _options.MaxCompaniesPerRun - depth.Count);

        return new NewsJudgmentCandidatePlan(
            depth,
            breadth.Candidates,
            companiesInUniverse: universe.Count,
            breadthDroppedByCapacityValve: breadth.DroppedByCapacityValve,
            capacityValve: _options.MaxCompaniesPerRun);
    }
}
