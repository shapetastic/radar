using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Tests.NewsRisk;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 187 §2 — the ONE per-run candidate-selection seam. The planner must REUSE the spec-179 §3
/// <see cref="NewsRiskCandidateSelector"/> rather than reimplement selection policy (a second copy would
/// let the typing priority order and the judged order drift apart, which is exactly the divergence this
/// slice exists to remove), and the plan it hands out must be frozen.
/// </summary>
public sealed class NewsJudgmentCandidatePlannerTests
{
    private static readonly Guid Alpha = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Beta = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Gamma = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static NewsJudgmentOptions Options(int maxCompaniesPerRun = 30) => new(
        outputDirectory: "unused",
        maxCompaniesPerRun: maxCompaniesPerRun,
        maxFamiliesPerJudgment: 50,
        maxJudgmentAttempts: 3,
        presentationJudge: "judge",
        presentationExtractor: "extractor",
        newsSearchCollectorName: "newssearch");

    private static IReadOnlyList<StrategyReportSection> Sections() =>
    [
        NewsRiskTestData.Section(
            "disclosure-led-v11",
            isPrimary: true,
            StrategyPurpose.Research,
            NewsRiskTestData.Row(1, Alpha, "Alpha Co", "ALPH"),
            NewsRiskTestData.Row(2, Beta, "Beta Co", "BETA")),
        NewsRiskTestData.Section(
            "narrative-led",
            isPrimary: false,
            StrategyPurpose.Research,
            NewsRiskTestData.Row(1, Gamma, "Gamma Co", "GAMM")),
        // A Comparator is never a judgment candidate — the selector's rule, inherited unchanged.
        NewsRiskTestData.Section(
            "market-cap-comparator",
            isPrimary: false,
            StrategyPurpose.Comparator,
            NewsRiskTestData.Row(1, Guid.NewGuid(), "Comparator Co", "CMP")),
    ];

    [Fact]
    public void Plan_IsExactlyTheSharedSelectorsOutput_AtTheResolvedBudget()
    {
        var sections = Sections();

        var plan = new NewsJudgmentCandidatePlanner(Options()).Plan(sections);

        Assert.Equal(
            NewsRiskCandidateSelector.Select(sections, 30).Select(c => c.CompanyId).ToList(),
            plan.CompanyIds);
        Assert.Equal([Alpha, Beta, Gamma], plan.CompanyIds);
        Assert.Equal(3, plan.Count);
    }

    [Fact]
    public void Plan_HonoursTheResolvedMaxCompaniesPerRun()
    {
        var plan = new NewsJudgmentCandidatePlanner(Options(maxCompaniesPerRun: 2)).Plan(Sections());

        Assert.Equal([Alpha, Beta], plan.CompanyIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoSections_PlanNothing_RatherThanFabricatingACandidate(bool nullSections)
    {
        var plan = new NewsJudgmentCandidatePlanner(Options())
            .Plan(nullSections ? null : []);

        Assert.Equal(0, plan.Count);
        Assert.Empty(plan.CompanyIds);
    }

    [Fact]
    public void Plan_IsFrozen_SoTheTwoPassesCanNeverBeHandedDifferentCandidates()
    {
        var mutable = new List<NewsRiskCandidate>
        {
            new(Alpha, "Alpha Co", "ALPH", []),
        };

        var plan = new NewsJudgmentCandidatePlan(mutable);
        mutable.Add(new NewsRiskCandidate(Beta, "Beta Co", "BETA", []));

        Assert.Equal([Alpha], plan.CompanyIds);
        Assert.Equal(1, plan.Count);
    }

    [Fact]
    public void CompanyIds_MirrorTheCandidateOrderExactly()
    {
        var plan = new NewsJudgmentCandidatePlanner(Options()).Plan(Sections());

        Assert.Equal(plan.Candidates.Select(c => c.CompanyId).ToList(), plan.CompanyIds);
    }
}
