using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 185 §5 at the live-artifact boundary (schema v3): each company's two-stage judgment cohorts render
/// independently with ALL FIVE completeness dimensions, the extraction-vs-judgment drop split, the
/// presentation-cohort marker state, and the display-only single-call vs two-stage category comparison —
/// no merged verdict anywhere. A company without judgments (the judgment step off) renders exactly as
/// before.
/// </summary>
public sealed class NewsRiskLiveJudgmentRenderTests
{
    private static readonly DateTimeOffset Now = NewsRiskTestData.SelectionAsOf;

    private static NewsRiskLiveReaderResult SingleCallResult(
        IReadOnlyList<NewsRiskCategory>? categories = null) => new(
        ReaderName: "reader-a",
        Provider: "test-provider",
        ModelId: "model-a",
        AssessmentId: Guid.NewGuid(),
        Status: NewsRiskAssessmentStatus.ThesisChallenged,
        AssessmentCutoffUtc: Now,
        RiskScore: 66,
        Categories: categories ?? [NewsRiskCategory.LiquidityOrGoingConcern],
        Claims: [],
        Rationale: null,
        Warnings: []);

    private static NewsRiskLiveJudgment Judgment(
        NewsJudgmentStatus status = NewsJudgmentStatus.Judged,
        IReadOnlyList<NewsJudgmentValidatedFinding>? findings = null,
        NewsTypingCompleteness typing = NewsTypingCompleteness.Backlog,
        IReadOnlyList<Guid>? trajectoryFactIds = null) => new(
        JudgeName: "judge-a",
        Provider: "openai",
        ModelId: "judge-model",
        Stage1CohortKey: "openai:extractor|news-typing-prompt-v1|news-typing-schema-v1|news-event-taxonomy-v1",
        JudgmentId: Guid.NewGuid(),
        Status: status,
        BusinessTrajectory: status == NewsJudgmentStatus.Judged ? NewsJudgmentTrajectory.Deteriorating : null,
        ChallengeStrength: findings is { Count: > 0 } ? 70 : null,
        Findings: findings ?? [],
        Rationale: "Factual read.",
        FindingsTotal: (findings?.Count ?? 0) + 1,
        FindingsAccepted: findings?.Count ?? 0,
        FindingsDropped: 1,
        FindingDropReasons: ["finding[1] cited-fact-not-supplied: 'bogus'"],
        Stage1FactsDroppedInWindow: 4,
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        ObservationSupply: NewsRiskAssessmentBundle.Complete,
        TypingCompleteness: typing,
        FamilyBundle: NewsJudgmentFamilyBundle.Capped,
        Families: [new NewsJudgmentFamilyRef(Guid.NewGuid(), Guid.NewGuid(), 3, 2)],
        TrajectoryFactIds: trajectoryFactIds);

    private static NewsRiskLiveCompany Company(
        IReadOnlyList<NewsRiskLiveJudgment>? judgments, string? marker) => new(
        CompanyId: Guid.NewGuid(),
        CompanyName: "Eos Energy",
        Ticker: "EOSE",
        Selections: [new NewsRiskCandidateSelection("default", 1, Guid.NewGuid())],
        Articles:
        [
            new NewsRiskLiveArticle(
                Guid.NewGuid(), "Headline", "Example Wire", "https://example.com/a",
                NewsObservationCaptureMode.ProspectiveRss, "headline"),
        ],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        AssessmentBundle: NewsRiskAssessmentBundle.Complete,
        QualifyingArticleCount: 1,
        CoverageIssues: [],
        ReaderResults: [SingleCallResult()],
        Judgments: judgments,
        JudgmentMarker: marker);

    private static string Render(NewsRiskLiveCompany company) =>
        NewsRiskLiveArtifactRenderer.RenderMarkdown(new NewsRiskLiveDocument(
            SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
            RunId: Guid.NewGuid(),
            SelectionAsOfUtc: Now,
            Caveat: NewsRiskLiveDocument.LiveCaveat,
            Readers: ["reader-a (test-provider:model-a)"],
            Diagnostic: null,
            Companies: [company],
            GeneratedAtUtc: Now));

    /// <summary>
    /// Spec 195 §2 moved the tag on from the v3 this file originally pinned: the live company row now
    /// carries the current run's pre-collapse syndication measurement, and a reader has to be able to tell a
    /// v3 document (NOT RECORDED, hydrating null) from a v4 document that measured an honest zero.
    /// </summary>
    [Fact]
    public void SchemaVersion_IsBumpedToV4()
    {
        Assert.Equal("news-risk-live-v4", NewsRiskLiveDocument.CurrentSchemaVersion);
    }

    [Fact]
    public void JudgmentSection_RendersAllFiveCompletenessDimensions_AndTheErrorSplit()
    {
        var finding = new NewsJudgmentValidatedFinding(
            NewsRiskCategory.RegulatoryOrLegalSetback,
            NewsRiskSeverity.High,
            0.85,
            [Guid.NewGuid()],
            "Based solely on a plaintiff-firm solicitation.");
        var markdown = Render(Company(
            [Judgment(findings: [finding])],
            marker: "⚠ challenged (regulatory-or-legal-setback, high)"));

        Assert.Contains("### Two-stage judgment", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Completeness: archive capture Proven · search enumeration Complete · observation supply "
                + "Complete · typing Backlog · family bundle Capped",
            markdown, StringComparison.Ordinal);
        // The §3 error split: stage-1 fact drops beside this cohort's own finding-drop accounting.
        Assert.Contains(
            "error split — stage-1 facts dropped in window: 4; stage-2 findings dropped: 1 of 2",
            markdown, StringComparison.Ordinal);
        Assert.Contains("business trajectory Deteriorating", markdown, StringComparison.Ordinal);
        Assert.Contains("challenge strength 70", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "attribution caveat: Based solely on a plaintiff-firm solicitation.",
            markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Leaders marker (presentation cohort only): ⚠ challenged (regulatory-or-legal-setback, high)",
            markdown, StringComparison.Ordinal);
        // The exploratory caveat: no audited stage-1 sample exists yet (the dispatch note).
        Assert.Contains("exploratory until stage-1 recall is audited", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CategoryComparison_IsSideBySideDisplayOnly_NeverAMergedVerdict()
    {
        var finding = new NewsJudgmentValidatedFinding(
            NewsRiskCategory.RegulatoryOrLegalSetback, NewsRiskSeverity.High, 0.85, [Guid.NewGuid()], null);
        var markdown = Render(Company(
            [Judgment(findings: [finding])], marker: null));

        Assert.Contains(
            "### Single-call vs two-stage categories (factual, no merged verdict)",
            markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Single-call reader-a (test-provider:model-a): LiquidityOrGoingConcern",
            markdown, StringComparison.Ordinal);
        Assert.Contains(
            "Two-stage judge-a (openai:judge-model)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("majority", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompanyWithoutJudgments_RendersNoJudgmentSection()
    {
        var markdown = Render(Company(judgments: null, marker: null));

        Assert.DoesNotContain("Two-stage judgment", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Single-call vs two-stage", markdown, StringComparison.Ordinal);
    }

    // ── Spec 187 §1: the trajectory's own provenance renders beside it ──────────────────────────────

    [Fact]
    public void AV2Judgment_RendersTheCitedTrajectoryEvidence()
    {
        var factId = Guid.Parse("77770000-0000-4000-8000-000000000001");

        var markdown = Render(Company([Judgment(trajectoryFactIds: [factId])], marker: null));

        Assert.Contains(
            "Trajectory evidence: `" + factId.ToString("D") + "`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AV2UnknownJudgment_RendersAnExplicitEmptyEvidenceSet()
    {
        var markdown = Render(Company([Judgment(trajectoryFactIds: [])], marker: null));

        Assert.Contains("Trajectory evidence: none cited", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AV1Judgment_RendersNotRecordedUnderV1_NeverAnEmptyEvidenceSet()
    {
        // Spec 187 §1: a historical v1 record has NO such field. Rendering it as an empty v2 evidence set
        // would read as "the judge cited nothing", i.e. as proof of invalidity — a claim about a record
        // that was written before the question was ever asked. Nothing on disk is rewritten (AD-8).
        var markdown = Render(Company([Judgment(trajectoryFactIds: null)], marker: null));

        Assert.Contains(
            "Trajectory evidence: not recorded under news-judgment-v1", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Trajectory evidence: none cited", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonJudgedRecord_RendersNoTrajectoryEvidenceLine()
    {
        // There is no trajectory to evidence, so the artifact says nothing about one.
        var markdown = Render(
            Company([Judgment(NewsJudgmentStatus.AttemptsExhausted)], marker: null));

        Assert.DoesNotContain("Trajectory evidence:", markdown, StringComparison.Ordinal);
        Assert.Contains("Status: **AttemptsExhausted**", markdown, StringComparison.Ordinal);
    }
}
