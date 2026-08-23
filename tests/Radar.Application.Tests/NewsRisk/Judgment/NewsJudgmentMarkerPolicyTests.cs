using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 185 §4 — the marker policy is the ONLY producer of a leader row's semantic-read state: pure, total
/// (every input maps to exactly one of the three states), stale-aware, and typing-completeness-aware. The
/// model never chooses presentation, and an absent marker is unrepresentable.
/// </summary>
public sealed class NewsJudgmentMarkerPolicyTests
{
    private static readonly Guid RunId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static NewsJudgmentRecord Record(
        NewsJudgmentStatus status,
        Guid? runId = null,
        IReadOnlyList<NewsJudgmentValidatedFinding>? findings = null,
        NewsTypingCompleteness typingCompleteness = NewsTypingCompleteness.Complete) => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: Guid.NewGuid(),
        RunId: runId ?? RunId,
        CompanyId: Guid.NewGuid(),
        CompanyName: "Eos Energy",
        Ticker: "EOSE",
        JudgeName: "deepinfra-deepseek",
        Provider: "openai",
        ModelId: "deepseek-ai/DeepSeek-V4-Flash",
        PromptVersion: NewsJudgmentContract.PromptVersion,
        ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
        Stage1CohortKey: "stage1",
        TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
        CohortKey: "cohort",
        FamilySetHash: "hash",
        Families: [],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        ObservationSupply: NewsRiskAssessmentBundle.Complete,
        TypingCompleteness: typingCompleteness,
        FamilyBundle: NewsJudgmentFamilyBundle.Complete,
        CoverageIssues: [],
        Status: status,
        BusinessTrajectory: status == NewsJudgmentStatus.Judged ? NewsJudgmentTrajectory.Mixed : null,
        ChallengeStrength: findings is { Count: > 0 } ? 55 : null,
        Findings: findings ?? [],
        Rationale: null,
        FindingsTotal: findings?.Count ?? 0,
        FindingsAccepted: findings?.Count ?? 0,
        FindingsDropped: 0,
        FindingDropReasons: [],
        RawResponseHash: null,
        FailureDetail: null,
        Limits: new NewsJudgmentLimitsRecord(30, 50),
        ReusedFromJudgmentId: null,
        CreatedAtUtc: new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));

    private static NewsJudgmentValidatedFinding Finding(
        NewsRiskCategory category = NewsRiskCategory.RegulatoryOrLegalSetback,
        NewsRiskSeverity severity = NewsRiskSeverity.High,
        double confidence = 0.8) =>
        new(category, severity, confidence, [Guid.NewGuid()], null);

    [Fact]
    public void JudgedWithFindings_IsChallenged_SummarizingTheTopFindingAsKebabTokens()
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(
                NewsJudgmentStatus.Judged,
                findings:
                [
                    Finding(NewsRiskCategory.UnitEconomicsOrMargin, NewsRiskSeverity.Low, 0.9),
                    Finding(NewsRiskCategory.RegulatoryOrLegalSetback, NewsRiskSeverity.High, 0.7),
                ]),
            RunId);

        Assert.Equal(NewsJudgmentMarkerState.Challenged, marker.State);
        // Severity outranks confidence: the High/0.7 finding wins over the Low/0.9 one.
        Assert.Equal("⚠ challenged (regulatory-or-legal-setback, high)", marker.CellText);
    }

    [Fact]
    public void JudgedWithZeroFindings_IsTheNarrowNoChallengeWording()
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(Record(NewsJudgmentStatus.Judged), RunId);

        Assert.Equal(NewsJudgmentMarkerState.NoChallengeFound, marker.State);
        Assert.Equal("· no challenge found in supplied facts", marker.CellText);
    }

    [Theory]
    [InlineData(NewsTypingCompleteness.Backlog)]
    [InlineData(NewsTypingCompleteness.Failed)]
    public void NoChallenge_UnderIncompleteTyping_AppendsTheQualifier(NewsTypingCompleteness incomplete)
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(NewsJudgmentStatus.Judged, typingCompleteness: incomplete), RunId);

        Assert.Equal(
            "· no challenge found in supplied facts (typing incomplete)", marker.CellText);
    }

    [Fact]
    public void ChallengedMarker_NeverCarriesTheTypingIncompleteSuffix()
    {
        // The qualifier weakens an ABSENCE claim; a found challenge is a presence claim and stands.
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(
                NewsJudgmentStatus.Judged,
                findings: [Finding()],
                typingCompleteness: NewsTypingCompleteness.Backlog),
            RunId);

        Assert.Equal(NewsJudgmentMarkerState.Challenged, marker.State);
        Assert.DoesNotContain("typing incomplete", marker.CellText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NewsJudgmentStatus.InsufficientFacts, "insufficient-facts")]
    [InlineData(NewsJudgmentStatus.ProviderFailure, "provider-failure")]
    [InlineData(NewsJudgmentStatus.ParseFailure, "parse-failure")]
    [InlineData(NewsJudgmentStatus.ValidationFailed, "validation-failed")]
    public void EveryNonJudgedStatus_IsUnassessedWithItsReasonToken(
        NewsJudgmentStatus status, string reason)
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(Record(status), RunId);

        Assert.Equal(NewsJudgmentMarkerState.Unassessed, marker.State);
        Assert.Equal($"? unassessed ({reason})", marker.CellText);
    }

    [Fact]
    public void APriorRunsJudgment_IsStale_NeverCarriedOver()
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(NewsJudgmentStatus.Judged, runId: Guid.NewGuid(), findings: [Finding()]),
            RunId);

        Assert.Equal(NewsJudgmentMarkerState.Unassessed, marker.State);
        Assert.Equal("? unassessed (stale)", marker.CellText);
    }

    [Fact]
    public void NullRecord_IsNotACandidate()
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(null, RunId);

        Assert.Equal("? unassessed (not-a-candidate)", marker.CellText);
    }

    [Fact]
    public void EveryInput_ProducesExactlyOneOfTheThreeStates_TotalFunction()
    {
        foreach (var status in Enum.GetValues<NewsJudgmentStatus>())
        {
            var marker = NewsJudgmentMarkerPolicy.Derive(Record(status), RunId);
            Assert.True(Enum.IsDefined(marker.State));
            Assert.False(string.IsNullOrWhiteSpace(marker.CellText));
        }
    }

    [Fact]
    public void MarkerCellFor_IsTotal_OverNullModelPendingAndMissingEntries()
    {
        var companyId = Guid.NewGuid();

        Assert.Equal(
            "? unassessed (no-judgment)",
            NewsJudgmentMarkerReportModel.MarkerCellFor(null, companyId));
        Assert.Equal(
            "? unassessed (judgment-pending)",
            NewsJudgmentMarkerReportModel.MarkerCellFor(NewsJudgmentMarkerReportModel.Pending, companyId));
        Assert.Equal(
            "? unassessed (not-a-candidate)",
            NewsJudgmentMarkerReportModel.MarkerCellFor(
                new NewsJudgmentMarkerReportModel(
                    JudgmentPending: false,
                    Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>()),
                companyId));
    }
}
