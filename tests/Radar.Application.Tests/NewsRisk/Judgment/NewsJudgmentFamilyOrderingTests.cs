using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Companies;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// SPEC 220 — the judge's family budget is filled by COMPARISON BASIS, not by syndication volume
/// (<c>family-ordering-v2</c>), a blank challenge strength no longer discards a grounded judgment, and both
/// are counted on one aggregated line per pass.
/// <para>
/// The defect: <c>NewsJudgmentInputBuilder</c> ordered families by <c>MemberCount</c> first, and what gets
/// syndicated is boilerplate. On 2026-09-09, 46 of 83 five-family breadth reads were correct
/// <c>Unknown</c> abstentions over <c>NotQuantified</c> families, and on the full cohort 7 of 17 cited
/// trajectory facts sat outside the top five by member count — AEHR's at rank 28 of 50.
/// </para>
/// </summary>
public sealed class NewsJudgmentFamilyOrderingTests
{
    private static readonly Guid Company = Guid.Parse("a0000000-0000-4000-8000-000000000220");

    private static readonly DateTimeOffset AsOf = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- §1 the order

    /// <summary>
    /// The ACCEPTANCE-CRITERION regression, pinned to the shape it was found in: AEHR's 2026-09-09
    /// <c>Improving</c> judgment cited a fact at rank 28 of 50 by member count. Under the pre-220 order a
    /// five-family read could never have seen it; under v2 it is the first family supplied.
    /// </summary>
    [Fact]
    public void Aehr_ATrajectoryFactRankedTwentyEighthByMemberCount_IsInsideTheTopFive()
    {
        var pairs = new List<(FactFamilyRecord Family, NewsTypingFactRef Fact)>();
        for (var i = 0; i < 50; i++)
        {
            var memberCount = 50 - i; // rank by member count = i + 1
            pairs.Add(i switch
            {
                27 => Family(
                    "Quarterly revenue grew 18% year-over-year on semiconductor test demand.",
                    NewsFactComparisonBasis.StatedComparison,
                    memberCount),
                _ when i % 2 == 0 => Family(
                    FormattableString.Invariant($"Backlog stands at ${i + 1} million at quarter close."),
                    NewsFactComparisonBasis.LevelOnly,
                    memberCount),
                _ => Family(
                    FormattableString.Invariant($"Management will attend investor conference session-{i}."),
                    NewsFactComparisonBasis.NotQuantified,
                    memberCount),
            });
        }

        var trajectoryFamily = pairs[27].Family;
        var factsById = pairs.ToDictionary(p => p.Fact.Fact.FactId, p => p.Fact);
        var families = pairs.Select(p => p.Family).ToList();

        // MUTATION-PROOF: the pre-220 order, reproduced exactly, does not supply it at a budget of five.
        var pre220TopFive = families
            .OrderByDescending(f => f.MemberCount)
            .ThenByDescending(f => f.DistinctPublisherCount)
            .ThenBy(f => f.FamilyId)
            .Take(5)
            .Select(f => f.FamilyId);
        Assert.DoesNotContain(trajectoryFamily.FamilyId, pre220TopFive);

        var bundle = NewsJudgmentInputBuilder.Build(Company, families, factsById, maxFamiliesPerJudgment: 5);

        Assert.Equal(5, bundle.Families.Count);
        Assert.Equal(trajectoryFamily.FamilyId, bundle.Families[0].FamilyId);
        Assert.Equal(NewsFactComparisonBasis.StatedComparison, bundle.Families[0].ComparisonBasis);
        // Then the levels (rank 1), by member count — never the NotQuantified boilerplate.
        Assert.All(
            bundle.Families.Skip(1),
            f => Assert.Equal(NewsFactComparisonBasis.LevelOnly, f.ComparisonBasis));
        Assert.Equal(50, bundle.FamiliesAvailable);
        Assert.Equal(new NewsJudgmentBasisCounts(1, 0, 25, 24), bundle.FamiliesAvailableByBasis);
    }

    [Fact]
    public void WithinABasisClass_ThePre220OrderIsUnchanged()
    {
        // Six NotQuantified families with deliberate MemberCount and DistinctPublisherCount ties, so every
        // key of the pre-220 order is exercised.
        var pairs = new[]
        {
            Family("Board appoints a new independent director.", NewsFactComparisonBasis.NotQuantified, 3, 1, Id(6)),
            Family("Chief executive to speak at an investor forum.", NewsFactComparisonBasis.NotQuantified, 3, 2, Id(5)),
            Family("Company names a new general counsel.", NewsFactComparisonBasis.NotQuantified, 9, 1, Id(4)),
            Family("Company relocates its headquarters to Austin.", NewsFactComparisonBasis.NotQuantified, 3, 2, Id(3)),
            Family("Investor day scheduled for the autumn.", NewsFactComparisonBasis.NotQuantified, 1, 1, Id(2)),
            Family("Company joins an industry trade association.", NewsFactComparisonBasis.NotQuantified, 3, 1, Id(1)),
        };
        var families = pairs.Select(p => p.Family).ToList();
        var factsById = pairs.ToDictionary(p => p.Fact.Fact.FactId, p => p.Fact);

        var bundle = NewsJudgmentInputBuilder.Build(Company, families, factsById, 50);

        var pre220 = families
            .OrderByDescending(f => f.MemberCount)
            .ThenByDescending(f => f.DistinctPublisherCount)
            .ThenBy(f => f.FamilyId)
            .Select(f => f.FamilyId)
            .ToList();
        Assert.Equal(pre220, bundle.Families.Select(f => f.FamilyId).ToList());
    }

    [Fact]
    public void EventRanksAboveLevelOnly_AndStatedComparisonAndEventInterleaveByMemberCount()
    {
        var statedSmall = Family("Orders declined during the quarter.", NewsFactComparisonBasis.StatedComparison, 5);
        var eventMid = Family("Regulator approved the new manufacturing permit.", NewsFactComparisonBasis.Event, 9);
        var statedMid = Family("Gross margin improved versus the prior year.", NewsFactComparisonBasis.StatedComparison, 7);
        var levelHuge = Family("Backlog stands at $2.5 billion.", NewsFactComparisonBasis.LevelOnly, 100);
        var notQuantifiedHuge = Family("Company to present at an industry conference.", NewsFactComparisonBasis.NotQuantified, 200);
        var pairs = new[] { statedSmall, eventMid, statedMid, levelHuge, notQuantifiedHuge };

        var bundle = NewsJudgmentInputBuilder.Build(
            Company,
            pairs.Select(p => p.Family).ToList(),
            pairs.ToDictionary(p => p.Fact.Fact.FactId, p => p.Fact),
            50);

        // StatedComparison and Event share rank 0, so MemberCount decides between them; a 100-member level
        // and a 200-member boilerplate family both follow a 5-member stated comparison.
        Assert.Equal(
            [
                eventMid.Family.FamilyId,
                statedMid.Family.FamilyId,
                statedSmall.Family.FamilyId,
                levelHuge.Family.FamilyId,
                notQuantifiedHuge.Family.FamilyId,
            ],
            bundle.Families.Select(f => f.FamilyId).ToList());
    }

    [Fact]
    public void TheOrderIsTotal_SoEveryInputPermutationSuppliesTheSameFamiliesAndHash()
    {
        var pairs = new[]
        {
            Family("Revenue rose sharply this quarter.", NewsFactComparisonBasis.StatedComparison, 2, 1, Id(10)),
            Family("Company filed a lawsuit against a former supplier.", NewsFactComparisonBasis.Event, 2, 1, Id(11)),
            Family("Cash and equivalents totaled $310 million.", NewsFactComparisonBasis.LevelOnly, 4, 2, Id(12)),
            Family("Debt stands at $90 million.", NewsFactComparisonBasis.LevelOnly, 4, 2, Id(13)),
            Family("Management will attend a healthcare forum.", NewsFactComparisonBasis.NotQuantified, 8, 3, Id(14)),
            Family("The board declared its annual meeting date.", NewsFactComparisonBasis.NotQuantified, 8, 3, Id(15)),
        };
        var factsById = pairs.ToDictionary(p => p.Fact.Fact.FactId, p => p.Fact);
        var baseline = NewsJudgmentInputBuilder.Build(Company, [.. pairs.Select(p => p.Family)], factsById, 3);

        var random = new Random(220);
        for (var permutation = 0; permutation < 25; permutation++)
        {
            var shuffled = pairs.Select(p => p.Family).OrderBy(_ => random.Next()).ToList();
            var bundle = NewsJudgmentInputBuilder.Build(Company, shuffled, factsById, 3);

            Assert.Equal(
                baseline.Families.Select(f => f.FamilyId).ToList(),
                bundle.Families.Select(f => f.FamilyId).ToList());
            Assert.Equal(baseline.FamilySetHash, bundle.FamilySetHash);
            Assert.Equal(baseline.FamiliesAvailableByBasis, bundle.FamiliesAvailableByBasis);
        }
    }

    [Fact]
    public void AnUnresolvableRepresentative_IsSkipped_AndCountedNowhereAsResolvable()
    {
        var resolvable = Family("Backlog stands at $40 million.", NewsFactComparisonBasis.LevelOnly, 1);
        // A stated comparison — the class that would lead — whose representative is NOT in the window index.
        var orphan = Family("Revenue doubled versus last year.", NewsFactComparisonBasis.StatedComparison, 50);

        var bundle = NewsJudgmentInputBuilder.Build(
            Company,
            [orphan.Family, resolvable.Family],
            new Dictionary<Guid, NewsTypingFactRef> { [resolvable.Fact.Fact.FactId] = resolvable.Fact },
            50);

        Assert.Equal(resolvable.Family.FamilyId, Assert.Single(bundle.Families).FamilyId);
        Assert.Equal(1, bundle.FamiliesAvailable);
        Assert.Equal(new NewsJudgmentBasisCounts(0, 0, 1, 0), bundle.FamiliesAvailableByBasis);
        Assert.Equal(NewsJudgmentFamilyBundle.Complete, bundle.FamilyBundle);
    }

    [Fact]
    public void TheBundleCountsAvailableFamiliesPerBasis_AndWithheldIsAvailableMinusSupplied()
    {
        var pairs = new[]
        {
            Family("Revenue grew versus the prior year.", NewsFactComparisonBasis.StatedComparison, 1),
            Family("Operating margin narrowed this quarter.", NewsFactComparisonBasis.StatedComparison, 1),
            Family("Company awarded a federal contract.", NewsFactComparisonBasis.Event, 1),
            Family("Backlog stands at $2.5 billion.", NewsFactComparisonBasis.LevelOnly, 1),
            Family("Cash totaled $310 million at period end.", NewsFactComparisonBasis.LevelOnly, 1),
            Family("Headcount reached 1,200 employees.", NewsFactComparisonBasis.LevelOnly, 1),
            Family("Chief executive to speak at an investor forum.", NewsFactComparisonBasis.NotQuantified, 1),
            Family("Board appoints a new independent director.", NewsFactComparisonBasis.NotQuantified, 1),
            Family("Company relocates its headquarters to Austin.", NewsFactComparisonBasis.NotQuantified, 1),
            Family("Investor day scheduled for the autumn.", NewsFactComparisonBasis.NotQuantified, 1),
        };

        var bundle = NewsJudgmentInputBuilder.Build(
            Company,
            [.. pairs.Select(p => p.Family)],
            pairs.ToDictionary(p => p.Fact.Fact.FactId, p => p.Fact),
            maxFamiliesPerJudgment: 4);

        var available = bundle.FamiliesAvailableByBasis!;
        var supplied = NewsJudgmentBasisCounts.Of(bundle.Families.Select(f => f.ComparisonBasis));

        Assert.Equal(new NewsJudgmentBasisCounts(2, 1, 3, 4), available);
        Assert.Equal(bundle.FamiliesAvailable, available.Sum());
        Assert.Equal(new NewsJudgmentBasisCounts(2, 1, 1, 0), supplied);
        // Withholding four NotQuantified families is a different fact from withholding four comparisons.
        Assert.Equal(new NewsJudgmentBasisCounts(0, 0, 2, 4), available.Minus(supplied));
        Assert.Equal(NewsJudgmentFamilyBundle.Capped, bundle.FamilyBundle);
    }

    [Fact]
    public void AnUndefinedBasis_IsNeverSilentlyRankedOrCounted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewsJudgmentFamilyOrdering.BasisRank((NewsFactComparisonBasis)0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewsJudgmentBasisCounts.Of([(NewsFactComparisonBasis)99]));
    }

    [Fact]
    public void TheOrderingVersion_JoinsTheCohortKey_AndThereforeOnlyTheEnabledNewsSegment()
    {
        Assert.Equal("family-ordering-v2", NewsJudgmentFamilyOrdering.Version);

        var key = NewsJudgmentContract.CohortKey("openai", "judge-model", "stage1");
        Assert.EndsWith("|ordering=family-ordering-v2", key, StringComparison.Ordinal);

        // It reaches ScoringConfigVersion ONLY through the enabled news= segment (the presentation cohort
        // key); the disabled segment carries no cohort key, which is what makes the AI-OFF pins immovable.
        Assert.Contains(
            NewsJudgmentFamilyOrdering.Version,
            NewsJudgmentScoringIdentityFactory.ForPresentationCohort(key).Segment,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ordering",
            NewsJudgmentScoringIdentity.Disabled.Segment,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- §2/§3 the pass line

    [Fact]
    public async Task ThePassEmitsOneBasisLine_WithAvailableSuppliedWithheldAndNotStatedCounts()
    {
        var (depth, breadth, plan, typing, runId) = Scenario();
        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(RespondWithFindingNoStrengthForBreadth(breadth.Name));

        var result = await generator.GenerateAsync(runId, plan, typing, CancellationToken.None);

        var line = Assert.Single(BasisLines(harness));
        Assert.Contains("(" + NewsJudgmentFamilyOrdering.Version + ", ", line, StringComparison.Ordinal);
        Assert.Contains(
            "FamiliesByBasisAvailable breadth [StatedComparison 1 / Event 1 / LevelOnly 2 / NotQuantified 3] "
                + "full [StatedComparison 1 / Event 0 / LevelOnly 1 / NotQuantified 1]",
            line,
            StringComparison.Ordinal);
        Assert.Contains(
            "FamiliesByBasisSupplied breadth [StatedComparison 1 / Event 1 / LevelOnly 2 / NotQuantified 1] "
                + "full [StatedComparison 1 / Event 0 / LevelOnly 1 / NotQuantified 1]",
            line,
            StringComparison.Ordinal);
        Assert.Contains(
            "FamiliesWithheldByBudget breadth [StatedComparison 0 / Event 0 / LevelOnly 0 / NotQuantified 2] "
                + "full [StatedComparison 0 / Event 0 / LevelOnly 0 / NotQuantified 0]",
            line,
            StringComparison.Ordinal);
        Assert.Contains("0 assembled input(s) whose basis accounting was not recorded", line, StringComparison.Ordinal);
        Assert.Contains("ChallengeStrengthNotStated 1 breadth / 0 full", line, StringComparison.Ordinal);

        // The spec-219 coverage line is still emitted, unchanged, beside it.
        Assert.Single(harness.Logger.Entries, e => e.Message.Contains(
            ") coverage (" + NewsJudgmentCoveragePolicy.Version + ")", StringComparison.Ordinal));

        // The breadth judgment was ACCEPTED, not discarded: findings kept, strength NOT RECORDED.
        var breadthRecord = Assert.Single(result!.Judgments, j => j.CompanyId == breadth.Id);
        Assert.Equal(NewsJudgmentStatus.Judged, breadthRecord.Status);
        Assert.Equal(1, breadthRecord.FindingsAccepted);
        Assert.Null(breadthRecord.ChallengeStrength);
        Assert.Equal("news-judgment-v9", breadthRecord.SchemaVersion);
        Assert.Equal(new NewsJudgmentBasisCounts(1, 1, 2, 3), breadthRecord.FamiliesAvailableByBasis);

        var depthRecord = Assert.Single(result.Judgments, j => j.CompanyId == depth.Id);
        Assert.Equal(3, depthRecord.ChallengeStrength);
        Assert.Equal(new NewsJudgmentBasisCounts(1, 0, 1, 1), depthRecord.FamiliesAvailableByBasis);
    }

    [Fact]
    public async Task AReusedVerdict_IsNotCountedAsChallengeStrengthNotStated_ButItsFamiliesStillAre()
    {
        var (_, breadth, plan, typing, runId) = Scenario();
        var first = new JudgmentPassHarness(AsOf);
        await first.Build(RespondWithFindingNoStrengthForBreadth(breadth.Name))
            .GenerateAsync(runId, plan, typing, CancellationToken.None);

        // A LATER run over the SAME accrued store and the SAME family sets: both verdicts are cache hits.
        var second = new JudgmentPassHarness(AsOf, first.Store);
        var generator = second.Build(RespondWithFindingNoStrengthForBreadth(breadth.Name));
        await generator.GenerateAsync(Guid.NewGuid(), plan, typing, CancellationToken.None);

        Assert.Equal(0, second.Analyzer!.Calls);
        var line = Assert.Single(BasisLines(second));
        // Spec 188 §1: a replayed verdict is not current activity...
        Assert.Contains("ChallengeStrengthNotStated 0 breadth / 0 full", line, StringComparison.Ordinal);
        // ...but the families describe THIS run's assembly, so they are counted as before.
        Assert.Contains(
            "FamiliesByBasisAvailable breadth [StatedComparison 1 / Event 1 / LevelOnly 2 / NotQuantified 3]",
            line,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static Guid Id(int index) =>
        Guid.Parse(FormattableString.Invariant($"0f000000-0000-4000-8000-{index:D12}"));

    /// <summary>
    /// One family + its resolvable representative, with the basis the production classifier assigns
    /// ASSERTED up front — so a fixture statement can never silently land in a class the test did not mean.
    /// </summary>
    private static (FactFamilyRecord Family, NewsTypingFactRef Fact) Family(
        string statement,
        NewsFactComparisonBasis expected,
        int memberCount,
        int distinctPublisherCount = 1,
        Guid? familyId = null)
    {
        var factId = Guid.NewGuid();
        var fact = NewsJudgmentTestData.FactRef(Company, factId, statement);
        Assert.Equal(expected, StatementComparisonClassifier.Classify(statement, fact.Fact.EventTypes));

        var family = NewsJudgmentTestData.FamilyRecord(
            Company, factId, statement, memberCount, distinctPublisherCount);
        return (familyId is { } id ? family with { FamilyId = id } : family, fact);
    }

    private static IEnumerable<string> BasisLines(JudgmentPassHarness harness) =>
        harness.Logger.Entries
            .Select(e => e.Message)
            .Where(m => m.Contains(") families by comparison basis (", StringComparison.Ordinal));

    /// <summary>
    /// A depth company with one family per class but Event, and a breadth company with seven families
    /// (1 stated / 1 event / 2 level / 3 unquantified) read at the breadth budget of five. Statements are
    /// deliberately dissimilar so each forms its own family.
    /// </summary>
    private static (Company Depth, Company Breadth, NewsJudgmentCandidatePlan Plan, NewsTypingRunResult Typing, Guid RunId)
        Scenario()
    {
        var depth = JudgmentPlanning.Company(JudgmentPassFixture.CompanyId(0), "Alpha Co", "AAA");
        var breadth = JudgmentPlanning.Company(JudgmentPassFixture.CompanyId(1), "Beta Co", "BBB");
        var runId = Guid.NewGuid();

        string[] depthStatements =
        [
            "Quarterly revenue grew sharply versus the prior year.",
            "Backlog stands at $2.5 billion.",
            "Chief executive scheduled to speak at an investor forum in Boston.",
        ];
        string[] breadthStatements =
        [
            "Operating margin narrowed during the quarter.",
            "Regulator approved the company's new manufacturing permit.",
            "Cash and equivalents totaled $310 million at period close.",
            "Headcount reached 1,200 employees across three plants.",
            "Board appoints a new independent director with a banking background.",
            "Company schedules its quarterly earnings call for next Tuesday.",
            "Investor day will take place in the autumn at the Denver campus.",
        ];

        var facts = depthStatements
            .Select((s, i) => new JudgmentPassFact(depth.Id, JudgmentPassFixture.FactId(i), s))
            .Concat(breadthStatements.Select(
                (s, i) => new JudgmentPassFact(breadth.Id, JudgmentPassFixture.FactId(100 + i), s)))
            .ToList();

        IReadOnlyList<StrategyReportSection> sections =
        [
            NewsRiskTestData.Section(
                "disclosure-led-v11",
                isPrimary: true,
                StrategyPurpose.Research,
                [NewsRiskTestData.Row(1, depth.Id, depth.Name, depth.Ticker)]),
        ];

        return (
            depth,
            breadth,
            JudgmentPlanning.Plan(JudgmentPassFixture.Options(), sections, depth, breadth),
            JudgmentPassFixture.Typing(runId, facts),
            runId);
    }

    /// <summary>
    /// A grounded directional read citing every supplied family, with ONE finding on the first supplied fact.
    /// The breadth company's response omits the strength (the §2 shape); the depth company's states 3.
    /// </summary>
    private static Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> RespondWithFindingNoStrengthForBreadth(
        string breadthName) =>
        request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: request.CompanyName == breadthName ? null : 3,
                Findings:
                [
                    new NewsJudgmentModelFinding(
                        Category: "RegulatoryOrLegalSetback",
                        Severity: "High",
                        Confidence: 0.8,
                        FactIds: [request.Families[0].RepresentativeFactId.ToString("D")],
                        AttributionCaveat: null),
                ],
                Rationale: "The confirmed filings are adverse to the recent trajectory.",
                TrajectoryFactIds: [.. request.Families.Select(f => f.RepresentativeFactId.ToString("D"))]),
            "raw-hash",
            null);
}
