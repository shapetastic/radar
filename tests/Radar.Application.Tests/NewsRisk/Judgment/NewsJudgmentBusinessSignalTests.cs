using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Tests.News;
using Radar.Domain.Companies;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// SPEC 221 — a stock-price move is not a business trajectory. §1: families confined to the EXISTING
/// context-only event types are demoted in selection (<c>family-ordering-v3</c>), never dropped. §2: the judge
/// gains a <c>NoBusinessSignal</c> verdict distinct from <c>Unknown</c>, Radar records what it HANDED the judge
/// (<see cref="NewsJudgmentSuppliedBasisProfile"/>), and the disagreement between the two is counted. §3: one
/// aggregated line per pass, plus the residual <c>Unknown</c> worklist NAMED.
/// </summary>
public sealed class NewsJudgmentBusinessSignalTests
{
    private static readonly Guid Company = Guid.Parse("a0000000-0000-4000-8000-000000000221");

    private static readonly DateTimeOffset AsOf = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly NewsEventType[] MarketReaction = [NewsEventType.MarketReaction];

    private static readonly NewsEventType[] Legal = [NewsEventType.RegulatoryOrLegal];

    // ---------------------------------------------------------------- §1 the order

    /// <summary>
    /// The ACCEPTANCE-CRITERION regression, pinned to the shape it was found in: SENEA's 2026-09-09 judgment
    /// said "the only supplied fact with a StatedComparison is the 3.9% price increase and the all-time high".
    /// The classifier is right that the WORDING compares; the selector must still rank the price move below
    /// a business fact that states nothing directional.
    /// </summary>
    [Fact]
    public void Senea_AStockPriceMoveStatedAsAComparison_RanksBelowABusinessNotQuantifiedFamily()
    {
        var priceMove = Family(
            "Shares rose 3.9% to an all-time high.", NewsFactComparisonBasis.StatedComparison, 40, MarketReaction);
        var director = Family(
            "Board appoints a new independent director.", NewsFactComparisonBasis.NotQuantified, 1, Legal);
        var pairs = new[] { priceMove, director };

        // MUTATION-PROOF: under v2 (basis first) the price move would have filled a one-family budget.
        Assert.True(
            NewsJudgmentFamilyOrdering.BasisRank(NewsFactComparisonBasis.StatedComparison)
                < NewsJudgmentFamilyOrdering.BasisRank(NewsFactComparisonBasis.NotQuantified));

        var bundle = Build(pairs, maxFamilies: 1);

        Assert.Equal(director.Family.FamilyId, Assert.Single(bundle.Families).FamilyId);
        Assert.Equal(2, bundle.FamiliesAvailable);
        Assert.Equal(1, bundle.FamiliesNonBusinessAvailable);
        Assert.Equal(1, bundle.FamiliesNonBusinessDemotedBySelection);
        Assert.Equal(0, bundle.FamiliesWithNoEventTypesAvailable);

        // The ComparisonBasis LINE the judge would see for the price move is untouched: it still says
        // StatedComparison. Spec 221 changes which families are picked, never what the judge is told.
        var full = Build(pairs, maxFamilies: 50);
        Assert.Equal(
            [director.Family.FamilyId, priceMove.Family.FamilyId],
            full.Families.Select(f => f.FamilyId).ToList());
        Assert.Equal(NewsFactComparisonBasis.StatedComparison, full.Families[1].ComparisonBasis);
        Assert.Equal(0, full.FamiliesNonBusinessDemotedBySelection);
    }

    [Fact]
    public void AMixedFamily_MarketReactionPlusEarnings_IsBusiness_AndKeepsItsBasisRank()
    {
        NewsEventType[] mixed = [NewsEventType.MarketReaction, NewsEventType.EarningsOrGuidance];
        var reactionOnResults = Family(
            "Shares jumped after revenue rose 12% versus the prior year.",
            NewsFactComparisonBasis.StatedComparison,
            1,
            mixed);
        var level = Family("Backlog stands at $2.5 billion.", NewsFactComparisonBasis.LevelOnly, 90, Legal);

        Assert.False(NewsJudgmentFamilyOrdering.IsNonBusiness(mixed));
        Assert.Equal(0, NewsJudgmentFamilyOrdering.ClassRank(NewsFactComparisonBasis.StatedComparison, mixed));

        var bundle = Build([reactionOnResults, level], maxFamilies: 1);

        Assert.Equal(reactionOnResults.Family.FamilyId, Assert.Single(bundle.Families).FamilyId);
        Assert.Equal(0, bundle.FamiliesNonBusinessAvailable);
    }

    [Fact]
    public void AFamilyWithNoEventTypes_IsNotDemoted_AndIsCounted()
    {
        var untyped = Family("Orders declined during the quarter.", NewsFactComparisonBasis.StatedComparison, 1, []);
        var level = Family("Backlog stands at $2.5 billion.", NewsFactComparisonBasis.LevelOnly, 90, Legal);

        // "We cannot tell" must not read as "we can reject" — the existing carve-out, reused.
        Assert.False(NewsJudgmentFamilyOrdering.IsNonBusiness([]));
        Assert.Equal(0, NewsJudgmentFamilyOrdering.ClassRank(NewsFactComparisonBasis.StatedComparison, []));

        var bundle = Build([untyped, level], maxFamilies: 1);

        Assert.Equal(untyped.Family.FamilyId, Assert.Single(bundle.Families).FamilyId);
        Assert.Equal(1, bundle.FamiliesWithNoEventTypesAvailable);
        Assert.Equal(0, bundle.FamiliesNonBusinessAvailable);

        var profile = NewsJudgmentSuppliedBasisProfile.Of(bundle.Families);
        Assert.Equal(1, profile.WithNoEventTypes);
        Assert.Equal(new NewsJudgmentBasisCounts(1, 0, 0, 0), profile.Business);
        Assert.Equal(NewsJudgmentBasisCounts.Zero, profile.NonBusiness);
    }

    [Fact]
    public void ACompanyWithOnlyMarketReactionFamilies_IsDemotedNeverDropped_AndStillFillsItsBudget()
    {
        var pairs = Enumerable.Range(0, 7)
            .Select(i => Family(
                FormattableString.Invariant($"Shares rose {i + 1}% in session {i}."),
                NewsFactComparisonBasis.StatedComparison,
                10 - i,
                MarketReaction))
            .ToArray();

        var bundle = Build(pairs, maxFamilies: 5);

        Assert.Equal(5, bundle.Families.Count);
        Assert.Equal(NewsJudgmentFamilyBundle.Capped, bundle.FamilyBundle);
        Assert.Equal(7, bundle.FamiliesAvailable);
        Assert.Equal(7, bundle.FamiliesNonBusinessAvailable);
        // Every family is non-business, so the v2 and v3 orders coincide: nothing was demoted OUT.
        Assert.Equal(0, bundle.FamiliesNonBusinessDemotedBySelection);
        Assert.Equal(new NewsJudgmentBasisCounts(7, 0, 0, 0), bundle.FamiliesAvailableByBasis);
        Assert.Equal(
            pairs.Take(5).Select(p => p.Family.FamilyId).ToList(),
            bundle.Families.Select(f => f.FamilyId).ToList());
    }

    [Fact]
    public void WithinEveryClass_AnAllBusinessInput_IsByteIdenticalToTheV2Order()
    {
        var pairs = new[]
        {
            Family("Revenue rose sharply this quarter.", NewsFactComparisonBasis.StatedComparison, 2, Legal, 1, Id(1)),
            Family("Company filed a lawsuit against a former supplier.", NewsFactComparisonBasis.Event, 2, Legal, 1, Id(2)),
            Family("Cash and equivalents totaled $310 million.", NewsFactComparisonBasis.LevelOnly, 4, Legal, 2, Id(3)),
            Family("Debt stands at $90 million.", NewsFactComparisonBasis.LevelOnly, 4, Legal, 2, Id(4)),
            Family("Management will attend a healthcare forum.", NewsFactComparisonBasis.NotQuantified, 8, Legal, 3, Id(5)),
            Family("The board declared its annual meeting date.", NewsFactComparisonBasis.NotQuantified, 8, Legal, 3, Id(6)),
        };

        var bundle = Build(pairs, maxFamilies: 50);

        var v2 = pairs
            .OrderBy(p => NewsJudgmentFamilyOrdering.BasisRank(
                StatementComparisonClassifier.Classify(p.Fact.Fact.Statement, p.Fact.Fact.EventTypes)))
            .ThenByDescending(p => p.Family.MemberCount)
            .ThenByDescending(p => p.Family.DistinctPublisherCount)
            .ThenBy(p => p.Family.FamilyId)
            .Select(p => p.Family.FamilyId)
            .ToList();
        Assert.Equal(v2, bundle.Families.Select(f => f.FamilyId).ToList());
        Assert.Equal(0, bundle.FamiliesNonBusinessAvailable);
        Assert.Equal(0, bundle.FamiliesNonBusinessDemotedBySelection);
    }

    [Fact]
    public void WithinTheNonBusinessClass_TheOrderIsMemberCountThenPublishersThenId_WhateverTheBasis()
    {
        var chatter = Family(
            "The stock was among the most active names on Tuesday.",
            NewsFactComparisonBasis.NotQuantified,
            50,
            MarketReaction,
            1,
            Id(10));
        var rise = Family("Shares rose 5% after hours.", NewsFactComparisonBasis.StatedComparison, 3, MarketReaction, 1, Id(11));

        var bundle = Build([rise, chatter], maxFamilies: 50);

        // Both rank 3: the StatedComparison wording earns a non-business family nothing.
        Assert.Equal(
            [chatter.Family.FamilyId, rise.Family.FamilyId],
            bundle.Families.Select(f => f.FamilyId).ToList());
    }

    [Fact]
    public void NonBusinessDemotedBySelection_CountsOnlyNonBusinessFamiliesTheV2OrderWouldHaveSupplied()
    {
        var a = Family("Shares rose 10% on heavy volume.", NewsFactComparisonBasis.StatedComparison, 10, MarketReaction);
        var b = Family("Shares climbed to a 12-month high.", NewsFactComparisonBasis.StatedComparison, 9, MarketReaction);
        var c = Family("Quarterly revenue grew 18% year-over-year.", NewsFactComparisonBasis.StatedComparison, 1, Legal);
        var d = Family("Board appoints a new independent director.", NewsFactComparisonBasis.NotQuantified, 5, Legal);
        var e = Family("Backlog stands at $2.5 billion.", NewsFactComparisonBasis.LevelOnly, 2, Legal);
        var f = Family(
            "The stock was among the most active names on Tuesday.",
            NewsFactComparisonBasis.NotQuantified,
            100,
            MarketReaction);

        var bundle = Build([a, b, c, d, e, f], maxFamilies: 3);

        // v2 top three: A, B, C (basis 0 by member count). v3 top three: C (business SC), E (business level),
        // D (business unquantified). A and B were in v2's budget and are not in v3's: two demoted. F was never
        // inside v2's budget, so it does not count.
        Assert.Equal(
            [c.Family.FamilyId, e.Family.FamilyId, d.Family.FamilyId],
            bundle.Families.Select(x => x.FamilyId).ToList());
        Assert.Equal(3, bundle.FamiliesNonBusinessAvailable);
        Assert.Equal(2, bundle.FamiliesNonBusinessDemotedBySelection);
        Assert.Equal(NewsJudgmentBasisCounts.Zero, NewsJudgmentSuppliedBasisProfile.Of(bundle.Families).NonBusiness);
    }

    [Fact]
    public void TheNonBusinessRule_IsTheExistingContextOnlySet_ForEveryEventType()
    {
        foreach (var type in Enum.GetValues<NewsEventType>())
        {
            Assert.Equal(
                NewsJudgmentContextOnlyEventTypes.Contains(type),
                NewsJudgmentFamilyOrdering.IsNonBusiness([type]));
        }

        // ManagementOrGovernance carries insider transactions (spec 93) and is correctly NOT context-only.
        Assert.False(NewsJudgmentFamilyOrdering.IsNonBusiness([NewsEventType.ManagementOrGovernance]));
        // An undefined basis still throws on a non-business family: demotion never launders an unranked value.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewsJudgmentFamilyOrdering.ClassRank((NewsFactComparisonBasis)0, MarketReaction));
    }

    /// <summary>
    /// Acceptance criterion: <see cref="NewsJudgmentContextOnlyEventTypes"/> is REUSED, not copied — no second
    /// list of its four tokens exists in production code. A source scan, because a copied list compiles fine
    /// and drifts silently (CLAUDE.md: never duplicate a value that code defines).
    /// </summary>
    [Fact]
    public void NoSecondListOfTheFourContextOnlyTokens_ExistsInProductionCode()
    {
        var source = Path.Combine(NewsObservationArchitectureGuardTests.FindRepositoryRoot(), "src");
        var tokens = NewsJudgmentContextOnlyEventTypes.Members
            .Select(m => "NewsEventType." + m)
            .ToArray();
        Assert.Equal(4, tokens.Length);

        var declaringFiles = Directory
            .EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return tokens.All(t => text.Contains(t, StringComparison.Ordinal));
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["NewsJudgmentSchema.cs"], declaringFiles);
    }

    // ---------------------------------------------------------------- §2b the verdict

    [Fact]
    public void NoBusinessSignal_WithNoCitations_IsJudged_WithNoBasis()
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "NoBusinessSignal",
            strength: null,
            findings: [],
            rationale: "Every supplied fact is a share-price move or an analyst label; none concerns the business.",
            trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsJudgmentTrajectory.NoBusinessSignal, result.BusinessTrajectory);
        Assert.Empty(result.TrajectoryFactIds);
        Assert.Null(result.TrajectoryBasis);
        Assert.Null(NewsJudgmentValidator.TrajectoryBasisFor(
            NewsJudgmentTrajectory.NoBusinessSignal,
            [family.RepresentativeFactId],
            new Dictionary<Guid, NewsJudgmentInputFamily> { [family.RepresentativeFactId] = family },
            [],
            new Dictionary<Guid, NewsJudgmentReferenceValue>()));
    }

    [Fact]
    public void NoBusinessSignal_WithACitedTrajectoryFact_FailsUnderItsOwnNamedReason()
    {
        var family = NewsJudgmentTestData.Family(assertionStatus: NewsFactAssertionStatus.ConfirmedFiling);
        var response = NewsJudgmentTestData.Response(
            trajectory: "NoBusinessSignal",
            strength: null,
            findings: [],
            rationale: "Nothing supplied concerns the business.",
            trajectoryFactIds: [family.RepresentativeFactId.ToString("D")]);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FindingDropReasons,
            r => r.StartsWith(NewsJudgmentValidator.TrajectoryEvidenceWithNoBusinessSignalReason + ":", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.FindingDropReasons,
            r => r.Contains(NewsJudgmentValidator.TrajectoryEvidenceWithUnknownReason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void NoBusinessSignal_WithABlankRationale_Fails(string? rationale)
    {
        var family = NewsJudgmentTestData.Family();
        var response = NewsJudgmentTestData.Response(
            trajectory: "NoBusinessSignal", strength: null, findings: [], rationale: rationale, trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(result.FindingDropReasons, r => r.StartsWith("rationale-missing", StringComparison.Ordinal));
    }

    [Fact]
    public void NoBusinessSignal_WithAdviceLanguage_Fails()
    {
        var family = NewsJudgmentTestData.Family();
        var response = NewsJudgmentTestData.Response(
            trajectory: "NoBusinessSignal",
            strength: null,
            findings: [],
            rationale: "Nothing here concerns the business, so it is a safe bet to buy.",
            trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [family]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Contains(
            result.FindingDropReasons, r => r.StartsWith("rationale-advice-language", StringComparison.Ordinal));
        Assert.Null(result.Rationale);
    }

    [Fact]
    public void NoBusinessSignal_MapsToNoDirection_AndRendersItsOwnMarkerToken()
    {
        Assert.Null(NewsTrajectorySignalRules.DirectionFor(NewsJudgmentTrajectory.NoBusinessSignal));
        Assert.Equal("no-business-signal", NewsJudgmentMarkerPolicy.TrajectoryToken(NewsJudgmentTrajectory.NoBusinessSignal));
        Assert.NotEqual(
            NewsJudgmentMarkerPolicy.TrajectoryToken(NewsJudgmentTrajectory.Unknown),
            NewsJudgmentMarkerPolicy.TrajectoryToken(NewsJudgmentTrajectory.NoBusinessSignal));
    }

    // ---------------------------------------------------------------- §2a/§2c the derived helpers

    [Fact]
    public void NoDirectionalBasisSupplied_IsTrueOnlyWhenNoBusinessFamilyIsStatedComparisonOrEvent()
    {
        // A non-business StatedComparison (the SENEA price move) does not count as directional supply.
        var onlyPriceMove = new NewsJudgmentSuppliedBasisProfile(
            Business: new NewsJudgmentBasisCounts(0, 0, 2, 3),
            NonBusiness: new NewsJudgmentBasisCounts(1, 0, 0, 0),
            WithNoEventTypes: 0);
        Assert.True(NewsJudgmentRecord.NoDirectionalBasisSupplied(onlyPriceMove));

        Assert.False(NewsJudgmentRecord.NoDirectionalBasisSupplied(
            onlyPriceMove with { Business = new NewsJudgmentBasisCounts(0, 1, 0, 0) }));
        Assert.False(NewsJudgmentRecord.NoDirectionalBasisSupplied(
            onlyPriceMove with { Business = new NewsJudgmentBasisCounts(1, 0, 0, 0) }));
    }

    [Fact]
    public void ClassifierSaidDirectionalJudgeSaidNoBusinessSignal_RequiresJudged_NoBusinessSignal_AndBusinessDirectionalSupply()
    {
        var directional = new NewsJudgmentSuppliedBasisProfile(
            new NewsJudgmentBasisCounts(1, 0, 0, 0), NewsJudgmentBasisCounts.Zero, 0);
        var nothingDirectional = new NewsJudgmentSuppliedBasisProfile(
            new NewsJudgmentBasisCounts(0, 0, 0, 4), new NewsJudgmentBasisCounts(1, 0, 0, 0), 0);

        Assert.True(NewsJudgmentRecord.ClassifierSaidDirectionalJudgeSaidNoBusinessSignal(
            NewsJudgmentStatus.Judged, NewsJudgmentTrajectory.NoBusinessSignal, directional));
        Assert.False(NewsJudgmentRecord.ClassifierSaidDirectionalJudgeSaidNoBusinessSignal(
            NewsJudgmentStatus.Judged, NewsJudgmentTrajectory.NoBusinessSignal, nothingDirectional));
        Assert.False(NewsJudgmentRecord.ClassifierSaidDirectionalJudgeSaidNoBusinessSignal(
            NewsJudgmentStatus.Judged, NewsJudgmentTrajectory.Unknown, directional));
        Assert.False(NewsJudgmentRecord.ClassifierSaidDirectionalJudgeSaidNoBusinessSignal(
            NewsJudgmentStatus.ValidationFailed, NewsJudgmentTrajectory.NoBusinessSignal, directional));
    }

    // ---------------------------------------------------------------- §3 the pass lines

    [Fact]
    public async Task ThePassEmitsOneBusinessSignalLine_AndNamesTheResidualUnknownWorklist()
    {
        var depth = JudgmentPlanning.Company(JudgmentPassFixture.CompanyId(0), "Alpha Co", "AAA");
        var unknown = JudgmentPlanning.Company(JudgmentPassFixture.CompanyId(1), "Beta Co", "BBB");
        var noSignal = JudgmentPlanning.Company(JudgmentPassFixture.CompanyId(2), "Gamma Co", "CCC");
        var runId = Guid.NewGuid();

        var facts = new List<JudgmentPassFact>
        {
            new(depth.Id, JudgmentPassFixture.FactId(0), "Quarterly revenue fell sharply versus the prior year."),
            // BBB: a price move and a director appointment — nothing BUSINESS-directional was supplied.
            new(unknown.Id, JudgmentPassFixture.FactId(10), "Shares rose 3.9% to an all-time high.", MarketReaction),
            new(unknown.Id, JudgmentPassFixture.FactId(11), "Board appoints a new independent director."),
            // CCC: a business comparison WAS supplied, and the judge still says NoBusinessSignal — the 2c shape.
            new(noSignal.Id, JudgmentPassFixture.FactId(20), "Operating margin narrowed during the quarter."),
            new(noSignal.Id, JudgmentPassFixture.FactId(21), "The stock was among the most active names on Tuesday.", MarketReaction),
        };

        IReadOnlyList<StrategyReportSection> sections =
        [
            NewsRiskTestData.Section(
                "disclosure-led-v11",
                isPrimary: true,
                StrategyPurpose.Research,
                [NewsRiskTestData.Row(1, depth.Id, depth.Name, depth.Ticker)]),
        ];
        var plan = JudgmentPlanning.Plan(JudgmentPassFixture.Options(), sections, depth, unknown, noSignal);
        var typing = JudgmentPassFixture.Typing(runId, facts);

        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(request => Respond(request, depth.Name, unknown.Name));

        var result = await generator.GenerateAsync(runId, plan, typing, CancellationToken.None);

        var unknownRecord = Assert.Single(result!.Judgments, j => j.CompanyId == unknown.Id);
        Assert.Equal(NewsJudgmentTrajectory.Unknown, unknownRecord.BusinessTrajectory);
        Assert.Equal(
            new NewsJudgmentSuppliedBasisProfile(
                new NewsJudgmentBasisCounts(0, 0, 0, 1), new NewsJudgmentBasisCounts(1, 0, 0, 0), 0),
            unknownRecord.SuppliedBasisProfile);
        Assert.Equal(1, unknownRecord.FamiliesNonBusinessAvailable);
        Assert.Equal(0, unknownRecord.FamiliesNonBusinessDemotedBySelection);
        Assert.Equal(0, unknownRecord.FamiliesWithNoEventTypesAvailable);
        // The price move is supplied LAST (demoted, never dropped).
        Assert.Equal(JudgmentPassFixture.FactId(10), unknownRecord.Families[^1].RepresentativeFactId);

        var noSignalRecord = Assert.Single(result.Judgments, j => j.CompanyId == noSignal.Id);
        Assert.Equal(NewsJudgmentStatus.Judged, noSignalRecord.Status);
        Assert.Equal(NewsJudgmentTrajectory.NoBusinessSignal, noSignalRecord.BusinessTrajectory);

        var line = Assert.Single(Lines(harness, ") business signal ("));
        Assert.Contains("(" + NewsJudgmentFamilyOrdering.Version + ")", line, StringComparison.Ordinal);
        Assert.Contains("FamiliesNonBusinessAvailable 2 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("FamiliesNonBusinessSupplied 2 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("FamiliesNonBusinessDemotedBySelection 0 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("FamiliesWithNoEventTypes 0 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("0 assembled input(s) whose business-signal accounting was not recorded", line, StringComparison.Ordinal);
        Assert.Contains("Over the 2 breadth / 1 full judged verdict(s)", line, StringComparison.Ordinal);
        Assert.Contains("JudgmentsWithNoDirectionalBasisSupplied 1 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("JudgmentsNoBusinessSignal 1 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("ClassifierSaidDirectionalJudgeSaidNoBusinessSignal 1 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("Deteriorating 0 breadth / 1 full", line, StringComparison.Ordinal);
        Assert.Contains("Unknown 1 breadth / 0 full", line, StringComparison.Ordinal);
        Assert.Contains("0 judged verdict(s) whose supplied-basis profile was not recorded", line, StringComparison.Ordinal);

        var worklist = Assert.Single(Lines(harness, ") residual Unknown worklist:"));
        Assert.Contains(
            $"Breadth: BBB ({unknownRecord.JudgmentId:D}). Full: none.", worklist, StringComparison.Ordinal);
        Assert.Contains("scripts/audit-miss-diagnosis.ps1 -Ticker <T> -ScoreDate <D>", worklist, StringComparison.Ordinal);

        // The spec-219 coverage line and the spec-220 basis line are still emitted, unchanged, beside them.
        Assert.Single(Lines(harness, ") coverage (" + NewsJudgmentCoveragePolicy.Version + ")"));
        Assert.Single(Lines(harness, ") families by comparison basis ("));
    }

    // ---------------------------------------------------------------- helpers

    private static NewsJudgmentAnalysisOutcome Respond(
        NewsJudgmentAnalysisRequest request, string depthName, string unknownName)
    {
        var response = request.CompanyName == depthName
            ? new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "Filed results show revenue down versus the prior year.",
                TrajectoryFactIds: [request.Families[0].RepresentativeFactId.ToString("D")])
            : new NewsJudgmentModelResponse(
                BusinessTrajectory: request.CompanyName == unknownName ? "Unknown" : "NoBusinessSignal",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "The supplied facts were read as a whole.",
                TrajectoryFactIds: []);
        return new NewsJudgmentAnalysisOutcome(NewsJudgmentAnalysisFailure.None, response, "raw-hash", null);
    }

    private static IEnumerable<string> Lines(JudgmentPassHarness harness, string marker) =>
        harness.Logger.Entries
            .Select(e => e.Message)
            .Where(m => m.Contains(marker, StringComparison.Ordinal));

    private static NewsJudgmentInputBundle Build(
        IReadOnlyList<(FactFamilyRecord Family, NewsTypingFactRef Fact)> pairs, int maxFamilies) =>
        NewsJudgmentInputBuilder.Build(
            Company,
            [.. pairs.Select(p => p.Family)],
            pairs.ToDictionary(p => p.Fact.Fact.FactId, p => p.Fact),
            maxFamilies);

    private static Guid Id(int index) =>
        Guid.Parse(FormattableString.Invariant($"0f000000-0000-4000-8000-{index:D12}"));

    /// <summary>
    /// One family + its resolvable representative with the given event types, the production classifier's
    /// basis ASSERTED up front so a fixture statement can never land in a class the test did not mean.
    /// </summary>
    private static (FactFamilyRecord Family, NewsTypingFactRef Fact) Family(
        string statement,
        NewsFactComparisonBasis expected,
        int memberCount,
        IReadOnlyList<NewsEventType> eventTypes,
        int distinctPublisherCount = 1,
        Guid? familyId = null)
    {
        var factId = Guid.NewGuid();
        var fact = NewsJudgmentTestData.FactRef(Company, factId, statement, eventTypes: eventTypes);
        Assert.Equal(expected, StatementComparisonClassifier.Classify(statement, eventTypes));

        var family = NewsJudgmentTestData.FamilyRecord(
            Company, factId, statement, memberCount, distinctPublisherCount, eventTypes: eventTypes);
        return (familyId is { } id ? family with { FamilyId = id } : family, fact);
    }
}
