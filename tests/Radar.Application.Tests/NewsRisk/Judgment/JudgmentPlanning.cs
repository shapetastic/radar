using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Reporting;
using Radar.Application.Tests.Efficacy;
using Radar.Domain.Companies;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// The ONE way these tests build a spec-187 §2 candidate plan now that planning is asynchronous and reads
/// the company universe (spec 219 §1). Extracted rather than repeated in each suite: seven suites drive the
/// real <see cref="NewsJudgmentCandidatePlanner"/>, and seven copies of the repository/logger wiring would
/// let one drift from the rest while all stayed green.
/// <para>
/// The universe repository is the shared <c>FakeCompanyRepository</c> (reused, not re-implemented) and it
/// completes synchronously, so <see cref="Plan"/> can block without any risk of deadlock. A test that
/// supplies NO universe gets NO breadth candidates, which is what keeps every pre-219 expectation in these
/// suites meaningful: they assert the DEPTH cohort, unchanged.
/// </para>
/// </summary>
internal static class JudgmentPlanning
{
    public static NewsJudgmentCandidatePlanner Planner(
        NewsJudgmentOptions options, params Company[] universe) =>
        new(
            options,
            new FakeCompanyRepository(universe),
            NullLogger<NewsJudgmentCandidatePlanner>.Instance);

    public static NewsJudgmentCandidatePlan Plan(
        NewsJudgmentOptions options,
        IReadOnlyList<StrategyReportSection>? sections,
        params Company[] universe) =>
        Planner(options, universe)
            .PlanAsync(sections, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>A universe member: id, name and the ticker the breadth order sorts on.</summary>
    public static Company Company(Guid id, string name, string? ticker) =>
        new(
            Id: id,
            Name: name,
            LegalName: null,
            Ticker: ticker,
            Exchange: null,
            CountryCode: null,
            Sector: null,
            Industry: null,
            Status: CompanyStatus.Active,
            CreatedAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            Themes: []);
}
