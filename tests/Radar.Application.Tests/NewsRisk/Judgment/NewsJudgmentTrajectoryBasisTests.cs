using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 214 §2 — the validator's <c>TrajectoryBasis</c>: computed ONLY for a Judged, DIRECTIONAL result
/// over the RESOLVED cited trajectory facts (Supported when at least one is a StatedComparison or Event,
/// LevelOnly otherwise); <c>null</c> = not applicable for Mixed, Unknown and every failure. LevelOnly is NOT
/// a validation failure — the judge's call is persisted verbatim and marked, and the materializer's
/// allowlist is what keeps it out of scoring.
/// </summary>
public sealed class NewsJudgmentTrajectoryBasisTests
{
    private static NewsJudgmentInputFamily Level(Guid? factId = null) =>
        NewsJudgmentTestData.Family(
            factId: factId,
            assertionStatus: NewsFactAssertionStatus.Reported,
            statement: "Backlog hits $2.5B");

    private static NewsJudgmentInputFamily Comparison(Guid? factId = null) =>
        NewsJudgmentTestData.Family(
            factId: factId,
            assertionStatus: NewsFactAssertionStatus.Reported,
            statement: "record revenue of $384 million");

    private static NewsJudgmentInputFamily Unquantified(Guid? factId = null) =>
        NewsJudgmentTestData.Family(
            factId: factId,
            assertionStatus: NewsFactAssertionStatus.Reported,
            statement: "The company will hold its annual meeting in October");

    private static NewsJudgmentInputFamily Event(Guid? factId = null)
    {
        const string Statement = "The FDA approved the product";
        return NewsJudgmentTestData.Family(
            factId: factId, assertionStatus: NewsFactAssertionStatus.Reported, statement: Statement) with
        {
            EventTypes = [NewsEventType.RegulatoryOrLegal],
            ComparisonBasis = StatementComparisonClassifier.Classify(
                Statement, [NewsEventType.RegulatoryOrLegal]),
        };
    }

    private static string[] Ids(params NewsJudgmentInputFamily[] families) =>
        [.. families.Select(f => f.RepresentativeFactId.ToString("D"))];

    [Fact]
    public void TheFixtures_ClassifyAsTheirNamesSay()
    {
        Assert.Equal(NewsFactComparisonBasis.LevelOnly, Level().ComparisonBasis);
        Assert.Equal(NewsFactComparisonBasis.StatedComparison, Comparison().ComparisonBasis);
        Assert.Equal(NewsFactComparisonBasis.NotQuantified, Unquantified().ComparisonBasis);
        Assert.Equal(NewsFactComparisonBasis.Event, Event().ComparisonBasis);
    }

    [Fact]
    public void ADirectionalRead_CitingAtLeastOneComparison_IsSupported()
    {
        var level = Level();
        var comparison = Comparison();
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: "Record revenue and a large backlog.",
            trajectoryFactIds: Ids(level, comparison));

        var result = NewsJudgmentValidator.Validate(response, [level, comparison]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsTrajectoryBasis.Supported, result.TrajectoryBasis);
    }

    [Fact]
    public void ADirectionalRead_CitingANumberlessEvent_IsSupported()
    {
        var evt = Event();
        var level = Level();
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: "An approval beside a backlog level.",
            trajectoryFactIds: Ids(evt, level));

        var result = NewsJudgmentValidator.Validate(response, [evt, level]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsTrajectoryBasis.Supported, result.TrajectoryBasis);
    }

    [Theory]
    [InlineData("Improving")]
    [InlineData("Deteriorating")]
    public void ADirectionalRead_CitingOnlyLevelsAndUnquantifiedFacts_IsLevelOnly_AndStillJudged(
        string trajectory)
    {
        // The Argan shape. NOT a validation failure: the call is persisted verbatim, with its citations,
        // and the basis marks it; the materializer's allowlist is the fail-closed step.
        var level = Level();
        var unquantified = Unquantified();
        var response = NewsJudgmentTestData.Response(
            trajectory: trajectory,
            strength: null,
            findings: [],
            rationale: "Backlog reached $2.5B, indicating strong future demand.",
            trajectoryFactIds: Ids(level, unquantified));

        var result = NewsJudgmentValidator.Validate(response, [level, unquantified]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(NewsTrajectoryBasis.LevelOnly, result.TrajectoryBasis);
        Assert.Equal(
            [level.RepresentativeFactId, unquantified.RepresentativeFactId], result.TrajectoryFactIds);
        Assert.Empty(result.FindingDropReasons);
        Assert.Equal("Backlog reached $2.5B, indicating strong future demand.", result.Rationale);
    }

    [Fact]
    public void AMixedRead_HasNoBasis_BecauseNoneIsApplicable()
    {
        var level = Level();
        var comparison = Comparison();
        var response = NewsJudgmentTestData.Response(
            trajectory: "Mixed",
            strength: null,
            findings: [],
            rationale: "Opposing facts.",
            trajectoryFactIds: Ids(level, comparison));

        var result = NewsJudgmentValidator.Validate(response, [level, comparison]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Null(result.TrajectoryBasis);
    }

    [Fact]
    public void AnUnknownRead_HasNoBasis()
    {
        var level = Level();
        var response = NewsJudgmentTestData.Response(
            trajectory: "Unknown",
            strength: null,
            findings: [],
            rationale: "Only a level was supplied; no direction is established.",
            trajectoryFactIds: []);

        var result = NewsJudgmentValidator.Validate(response, [level]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Null(result.TrajectoryBasis);
    }

    [Fact]
    public void AFailedValidation_HasNoBasis()
    {
        var comparison = Comparison();
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: null, // rationale-missing ⇒ ValidationFailed
            trajectoryFactIds: Ids(comparison));

        var result = NewsJudgmentValidator.Validate(response, [comparison]);

        Assert.Equal(NewsJudgmentStatus.ValidationFailed, result.Status);
        Assert.Null(result.TrajectoryBasis);
    }

    [Fact]
    public void TheBasis_IsComputedOverTheResolvedCitations_SoAPrefixCitationCounts()
    {
        // Spec 197's resolver expands a unique hexadecimal prefix to the supplied fact; the basis is then
        // read from THAT family — the resolved citation set, never the raw strings.
        var comparison = Comparison(Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001"));
        var level = Level(Guid.Parse("f9e8d7c6-0000-4000-8000-000000000002"));
        var response = NewsJudgmentTestData.Response(
            trajectory: "Improving",
            strength: null,
            findings: [],
            rationale: "Record revenue.",
            trajectoryFactIds: ["a1b2c3d4"]);

        var result = NewsJudgmentValidator.Validate(response, [comparison, level]);

        Assert.Equal(NewsJudgmentStatus.Judged, result.Status);
        Assert.Equal(1, result.FactIdPrefixExpansionCount);
        Assert.Equal(NewsTrajectoryBasis.Supported, result.TrajectoryBasis);
    }

    [Fact]
    public void TheRuleItself_IsOneStaticFunction_TotalOverEveryTrajectory()
    {
        var level = Level();
        var byId = new Dictionary<Guid, NewsJudgmentInputFamily> { [level.RepresentativeFactId] = level };

        var noReferences = new Dictionary<Guid, NewsJudgmentReferenceValue>();

        Assert.Equal(
            NewsTrajectoryBasis.LevelOnly,
            NewsJudgmentValidator.TrajectoryBasisFor(
                NewsJudgmentTrajectory.Improving, [level.RepresentativeFactId], byId, [], noReferences));
        Assert.Null(NewsJudgmentValidator.TrajectoryBasisFor(
            NewsJudgmentTrajectory.Mixed, [level.RepresentativeFactId], byId, [], noReferences));
        Assert.Null(NewsJudgmentValidator.TrajectoryBasisFor(
            NewsJudgmentTrajectory.Unknown, [], byId, [], noReferences));
    }
}
