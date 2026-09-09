using Radar.Domain.Companies;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// SPEC 219 §1 — the BREADTH half of judgment coverage: every company in the universe the depth cohort did
/// not already select, in one deterministic order, with no notion of "better" anywhere in it.
/// <para>
/// <b>The defect this closes.</b> Until this slice the judge's ONLY candidate source was
/// <see cref="NewsRiskCandidateSelector"/> — the spec-179 §3 top-five-rows-per-Research-section traversal,
/// built to pick names for a RISK AUDIT of what a human was about to be shown. Spec 194 then made the
/// judge's verdict the sole source of DIRECTIONAL <c>MediaAttention</c> for SCORING, and the selection rule
/// was never re-derived: ~19 of 102 companies were read, the other ~83 reached scoring as a count of
/// articles with no direction, and because <c>RadarScoreFormulaV8</c> discounts Opportunity by Attention,
/// heavy unread coverage LOWERED a rank, which pushed the company out of the top five, which is why the
/// coverage was never read. MarineMax (HZO) on 2026-07-31 — the largest miss in the store, +51.4% over 21
/// sessions, ~75 collapsed articles of takeover coverage — was never judged once.
/// </para>
/// <para>
/// <b>There is no ordering by desirability here, and that is the whole point.</b> No rank, no score, no
/// consensus across arms and no report type is reachable from this policy: it takes the company universe and
/// the ids the depth cohort already holds, and it returns the rest. The order exists only so two runs over
/// one store produce one order (AD-3), not to express a preference.
/// </para>
/// <para>
/// <b>No status filter.</b> The breadth pass enumerates the universe exactly as
/// <c>CollectionPass</c> does — <c>ICompanyRepository.GetAllAsync</c>, whole — because collection does not
/// filter by <see cref="CompanyStatus"/> either, and a judge that read a narrower universe than the
/// collector wrote evidence for would silently under-cover names whose evidence Radar holds.
/// </para>
/// </summary>
public static class NewsJudgmentCoveragePolicy
{
    /// <summary>
    /// The COVERAGE-POLICY identity token: which companies get a directional news read at all.
    /// <c>v1</c> was the implicit rank-gated policy (the spec-179 traversal, reused unchanged for scoring);
    /// <c>v2</c> is universal coverage with a capped depth. It is hashed into the <c>news=</c> segment of
    /// <c>ScoringConfigVersion</c> (spec 219 §6) through
    /// <c>NewsJudgmentScoringIdentityFactory.ForPresentationCohort</c>, which READS this constant rather
    /// than restating it, because a copied token goes stale silently.
    /// </summary>
    public const string Version = "news-judgment-coverage-v2";

    /// <summary>
    /// The breadth selection's outcome: the ordered candidates, and the count the capacity valve dropped.
    /// <see cref="DroppedByCapacityValve"/> is a MEASURED zero when the valve did not bite — never omitted,
    /// because a silently truncated universe is the defect this policy exists to remove.
    /// </summary>
    public readonly record struct BreadthSelection(
        IReadOnlyList<NewsRiskCandidate> Candidates, int DroppedByCapacityValve);

    /// <summary>
    /// Every company in <paramref name="universe"/> whose id is not already in
    /// <paramref name="depthCompanyIds"/>, ordered TICKER ASCENDING (ordinal; a null/blank ticker sorts
    /// LAST, after every tickered company) then COMPANY ID ascending, truncated to
    /// <paramref name="remainingCapacity"/> with the remainder COUNTED.
    /// <para>
    /// Pure and clock-free. A candidate carries an EMPTY selection list: no strategy selected it and none is
    /// invented — "shared ancestry, never consensus" (spec 179 §3) applies here as "no ancestry at all",
    /// which is exactly what a universe enumeration has.
    /// </para>
    /// </summary>
    public static BreadthSelection Select(
        IReadOnlyList<Company> universe,
        IReadOnlySet<Guid> depthCompanyIds,
        int remainingCapacity)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(depthCompanyIds);

        if (remainingCapacity < 0)
        {
            remainingCapacity = 0;
        }

        var eligible = universe
            .Where(c => !depthCompanyIds.Contains(c.Id))
            .OrderBy(c => string.IsNullOrWhiteSpace(c.Ticker) ? 1 : 0)
            // The untickered GROUP orders on company id alone: a blank ticker is "not recorded", so
            // comparing one blank spelling ("   ") against another (null) would order on an artefact of how
            // the absence was written rather than on anything about the company.
            .ThenBy(
                c => string.IsNullOrWhiteSpace(c.Ticker) ? string.Empty : c.Ticker,
                StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();

        var taken = eligible
            .Take(remainingCapacity)
            .Select(c => new NewsRiskCandidate(c.Id, c.Name, c.Ticker, []))
            .ToList();

        return new BreadthSelection(taken, eligible.Count - taken.Count);
    }
}
