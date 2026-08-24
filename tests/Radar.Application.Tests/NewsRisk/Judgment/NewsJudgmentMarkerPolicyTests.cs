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
        NewsTypingCompleteness typingCompleteness = NewsTypingCompleteness.Complete,
        NewsJudgmentTrajectory? trajectory = NewsJudgmentTrajectory.Mixed,
        Guid? judgmentId = null) => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: judgmentId ?? Guid.NewGuid(),
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
        BusinessTrajectory: status == NewsJudgmentStatus.Judged ? trajectory : null,
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
        Assert.Equal(
            "⚠ challenged (regulatory-or-legal-setback, high) · trajectory mixed", marker.CellText);
    }

    [Fact]
    public void JudgedWithZeroFindings_AndANonDeterioratingTrajectory_IsTheNarrowNoChallengeWording()
    {
        // Spec 186 §1: Mixed/Unknown/Improving + zero findings STAY no-challenge-found — a validated
        // judgment exists, and calling it unassessed would be a false statement about the read. It is
        // defensible precisely BECAUSE the trajectory token renders in the same cell.
        var marker = NewsJudgmentMarkerPolicy.Derive(Record(NewsJudgmentStatus.Judged), RunId);

        Assert.Equal(NewsJudgmentMarkerState.NoChallengeFound, marker.State);
        Assert.Equal("· no challenge found in supplied facts · trajectory mixed", marker.CellText);
    }

    [Theory]
    [InlineData(NewsJudgmentTrajectory.Mixed, "mixed")]
    [InlineData(NewsJudgmentTrajectory.Unknown, "unknown")]
    [InlineData(NewsJudgmentTrajectory.Improving, "improving")]
    public void JudgedWithZeroFindings_KeepsTheDot_ForEveryNonDeterioratingTrajectory(
        NewsJudgmentTrajectory trajectory, string token)
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(NewsJudgmentStatus.Judged, trajectory: trajectory), RunId);

        Assert.Equal(NewsJudgmentMarkerState.NoChallengeFound, marker.State);
        Assert.Equal("· no challenge found in supplied facts · trajectory " + token, marker.CellText);
    }

    [Fact]
    public void JudgedWithZeroFindings_AndADeterioratingTrajectory_IsChallenged_NeverTheDot()
    {
        // Spec 186 §1 fix 1: "no challenge found" is an ABSENCE claim, and rendering it beside the same
        // record's contrary presence evidence is the omission-bias failure the marker exists to prevent.
        // No finding is invented — the summary names the trajectory AXIS.
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(NewsJudgmentStatus.Judged, trajectory: NewsJudgmentTrajectory.Deteriorating), RunId);

        Assert.Equal(NewsJudgmentMarkerState.Challenged, marker.State);
        Assert.Equal(
            "⚠ challenged (business-trajectory-deteriorating) · trajectory deteriorating",
            marker.CellText);
        Assert.DoesNotContain("no challenge found", marker.CellText, StringComparison.Ordinal);
    }

    [Fact]
    public void DeterioratingWithZeroFindings_IsStillChallenged_UnderIncompleteTyping()
    {
        // The typing-incomplete qualifier weakens an ABSENCE claim; this row makes none.
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(
                NewsJudgmentStatus.Judged,
                typingCompleteness: NewsTypingCompleteness.Backlog,
                trajectory: NewsJudgmentTrajectory.Deteriorating),
            RunId);

        Assert.Equal(NewsJudgmentMarkerState.Challenged, marker.State);
        Assert.DoesNotContain("typing incomplete", marker.CellText, StringComparison.Ordinal);
    }

    [Fact]
    public void JudgedWithANullPersistedTrajectory_IsUnassessedInvalidRecord_NeverADotAndNeverUnknown()
    {
        // The validator REQUIRES the trajectory token to parse, so null-under-Judged can only be a
        // corrupted or hand-edited record (spec 186 §1). It is an INVALID state, not an unknown one.
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(NewsJudgmentStatus.Judged, trajectory: null), RunId);

        Assert.Equal(NewsJudgmentMarkerState.Unassessed, marker.State);
        Assert.Equal("? unassessed (invalid-record)", marker.CellText);
        Assert.DoesNotContain("unknown", marker.CellText, StringComparison.Ordinal);
        Assert.DoesNotContain("·", marker.CellText, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryJudgedMarker_CarriesTheTrajectoryToken_UniformlyAcrossBothJudgedStates()
    {
        // Uniform, never selective for bad news: the display is state-complete, so the dot can never
        // silently imply health.
        var challenged = NewsJudgmentMarkerPolicy.Derive(
            Record(
                NewsJudgmentStatus.Judged,
                findings: [Finding()],
                trajectory: NewsJudgmentTrajectory.Improving),
            RunId);
        var noChallenge = NewsJudgmentMarkerPolicy.Derive(
            Record(NewsJudgmentStatus.Judged, trajectory: NewsJudgmentTrajectory.Improving), RunId);

        Assert.Equal("improving", challenged.Trajectory);
        Assert.Equal("improving", noChallenge.Trajectory);
        Assert.EndsWith(" · trajectory improving", challenged.CellText, StringComparison.Ordinal);
        Assert.EndsWith(" · trajectory improving", noChallenge.CellText, StringComparison.Ordinal);

        foreach (var trajectory in Enum.GetValues<NewsJudgmentTrajectory>())
        {
            var marker = NewsJudgmentMarkerPolicy.Derive(
                Record(NewsJudgmentStatus.Judged, trajectory: trajectory), RunId);
            Assert.Contains(
                " · trajectory " + NewsJudgmentMarkerPolicy.TrajectoryToken(trajectory),
                marker.CellText,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryUnassessedState_RendersNoTrajectoryToken()
    {
        // There is no completed read to describe, so the cell says nothing about the axis.
        foreach (var status in Enum.GetValues<NewsJudgmentStatus>().Where(
            s => s != NewsJudgmentStatus.Judged))
        {
            var marker = NewsJudgmentMarkerPolicy.Derive(Record(status), RunId);
            Assert.Null(marker.Trajectory);
            Assert.DoesNotContain("trajectory", marker.CellText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EverySameRunRecordDerivedMarker_CarriesItsJudgmentId_SoTheReportCanCiteIt()
    {
        var judgmentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        foreach (var status in Enum.GetValues<NewsJudgmentStatus>())
        {
            var marker = NewsJudgmentMarkerPolicy.Derive(
                Record(status, judgmentId: judgmentId), RunId);
            Assert.Equal(judgmentId, marker.JudgmentId);
        }

        // A record-less row and a PRIOR run's record cite nothing: neither describes this run's judgment.
        Assert.Null(NewsJudgmentMarkerPolicy.Derive(null, RunId).JudgmentId);
        Assert.Null(NewsJudgmentMarkerPolicy
            .Derive(Record(NewsJudgmentStatus.Judged, runId: Guid.NewGuid()), RunId).JudgmentId);
    }

    [Fact]
    public void TrajectoryEnum_ZeroValueIsTheDegradedState_NeverTheBestOne()
    {
        // Spec 186 §1's enum-zero sub-fix (the spec-182 convention): a record that hydrates as the default
        // must never read as the BEST state. Trajectory persists as TOKENS everywhere, so the member order
        // carries no persisted or wire meaning.
        Assert.Equal(NewsJudgmentTrajectory.Unknown, default(NewsJudgmentTrajectory));
        Assert.NotEqual(NewsJudgmentTrajectory.Improving, default(NewsJudgmentTrajectory));
    }

    [Theory]
    [InlineData(NewsTypingCompleteness.Backlog)]
    [InlineData(NewsTypingCompleteness.Failed)]
    public void NoChallenge_UnderIncompleteTyping_AppendsTheQualifier(NewsTypingCompleteness incomplete)
    {
        var marker = NewsJudgmentMarkerPolicy.Derive(
            Record(NewsJudgmentStatus.Judged, typingCompleteness: incomplete), RunId);

        Assert.Equal(
            "· no challenge found in supplied facts (typing incomplete) · trajectory mixed",
            marker.CellText);
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
