using Radar.Application.News;
using Radar.Application.NewsRisk;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 182 §3: the live renderer states all three completeness dimensions per company, derives the
/// permanently-narrow absence wording per run from that run's dimensions, and NO rendered state — at any
/// status, any dimension combination — ever reads as an "all-clear".
/// </summary>
public sealed class NewsRiskLiveArtifactRendererTests
{
    private static readonly DateTimeOffset Now = NewsRiskTestData.SelectionAsOf;

    private static NewsRiskLiveReaderResult Result(
        NewsRiskAssessmentStatus status,
        int? riskScore = null,
        IReadOnlyList<string>? warnings = null) => new(
        ReaderName: "reader-a",
        Provider: "test-provider",
        ModelId: "model-a",
        AssessmentId: Guid.NewGuid(),
        Status: status,
        AssessmentCutoffUtc: Now,
        RiskScore: riskScore,
        Categories: [],
        Claims: [],
        Rationale: null,
        Warnings: warnings ?? []);

    private static NewsRiskLiveCompany Company(
        NewsRiskAssessmentStatus status,
        NewsRiskArchiveCapture archiveCapture,
        NewsRiskSearchEnumeration searchEnumeration,
        NewsRiskAssessmentBundle assessmentBundle,
        int suppliedArticles = 2,
        int qualifying = 2) => new(
        CompanyId: Guid.NewGuid(),
        CompanyName: "Test Co",
        Ticker: "TST",
        Selections: [new NewsRiskCandidateSelection("default", 1, Guid.NewGuid())],
        Articles: Enumerable.Range(0, suppliedArticles)
            .Select(i => new NewsRiskLiveArticle(
                Guid.NewGuid(), $"Headline {i}", "Example Wire", "https://example.com/" + i,
                NewsObservationCaptureMode.ProspectiveRss, "headline"))
            .ToList(),
        ArchiveCapture: archiveCapture,
        SearchEnumeration: searchEnumeration,
        AssessmentBundle: assessmentBundle,
        QualifyingArticleCount: qualifying,
        CoverageIssues: [],
        ReaderResults: [Result(status, status == NewsRiskAssessmentStatus.ThesisChallenged ? 66 : null)]);

    private static NewsRiskLiveDocument Document(params NewsRiskLiveCompany[] companies) => new(
        SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
        RunId: Guid.NewGuid(),
        SelectionAsOfUtc: Now,
        Caveat: NewsRiskLiveDocument.LiveCaveat,
        Readers: ["reader-a (test-provider:model-a)"],
        Diagnostic: null,
        Companies: companies,
        GeneratedAtUtc: Now);

    /// <summary>Every dimension combination — the renderer must state all three at each of them.</summary>
    public static TheoryData<NewsRiskArchiveCapture, NewsRiskSearchEnumeration, NewsRiskAssessmentBundle>
        AllDimensionCombinations()
    {
        var data = new TheoryData<NewsRiskArchiveCapture, NewsRiskSearchEnumeration, NewsRiskAssessmentBundle>();
        foreach (var capture in Enum.GetValues<NewsRiskArchiveCapture>())
        {
            foreach (var search in Enum.GetValues<NewsRiskSearchEnumeration>())
            {
                foreach (var bundle in Enum.GetValues<NewsRiskAssessmentBundle>())
                {
                    data.Add(capture, search, bundle);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllDimensionCombinations))]
    public void NoRiskVerdict_RendersTheNarrowWording_AtEveryDimensionCombination_AndNeverAnAllClear(
        NewsRiskArchiveCapture capture,
        NewsRiskSearchEnumeration search,
        NewsRiskAssessmentBundle bundle)
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(Company(
            NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText, capture, search, bundle,
            suppliedArticles: 2, qualifying: bundle == NewsRiskAssessmentBundle.Capped ? 5 : 2)));

        // The absence wording is permanently scoped to the supplied text…
        Assert.Contains("No risk was supported by the supplied text.", markdown);
        Assert.Contains("not about the company", markdown);
        // …all three dimensions are stated…
        Assert.Contains($"archive capture {capture}", markdown);
        Assert.Contains($"search enumeration {search}", markdown);
        Assert.Contains($"assessment bundle {bundle}", markdown);
        // …a degraded combination additionally states the degradation — as KNOWN incompleteness only
        // when a dimension proves it, and as "not proven" when the degradation is unproven-only…
        if (!NewsRiskCompletenessDescription.IsBestState(capture, search, bundle))
        {
            Assert.Contains(
                NewsRiskCompletenessDescription.HasKnownIncompleteness(search, bundle)
                    ? "Supplied text is known to be incomplete"
                    : "Supplied text is not proven complete",
                markdown);
        }

        // …and nothing, at any combination, reads as an all-clear.
        AssertNoAllClear(markdown);
    }

    [Fact]
    public void ThesisChallenged_RendersWithTheDimensionsStatedBesideIt_NeverSuppressed()
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(Company(
            NewsRiskAssessmentStatus.ThesisChallenged,
            NewsRiskArchiveCapture.Proven,
            NewsRiskSearchEnumeration.Truncated,
            NewsRiskAssessmentBundle.Capped,
            suppliedArticles: 2,
            qualifying: 5)));

        Assert.Contains("Status: **ThesisChallenged**", markdown);
        Assert.Contains(
            "Completeness: archive capture Proven · search enumeration Truncated · assessment bundle "
                + "Capped (2 supplied of 5 qualifying available)",
            markdown);
        AssertNoAllClear(markdown);
    }

    [Fact]
    public void EveryStatus_RendersWithoutAnAllClear()
    {
        var companies = Enum.GetValues<NewsRiskAssessmentStatus>()
            .Where(s => s != ObsoleteIncompleteCoverage()) // never produced again (spec 182)
            .Select(s => Company(
                s,
                NewsRiskArchiveCapture.Proven,
                NewsRiskSearchEnumeration.Complete,
                NewsRiskAssessmentBundle.Complete))
            .ToArray();

        AssertNoAllClear(NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(companies)));
    }

    [Fact]
    public void NoRiskPresentation_IsAPureFunctionOfThisRunsDimensions_NotOfAnyCachedRun()
    {
        // The same raw verdict (status) under two different dimension sets must derive two different
        // presentations — the derivation consumes ONLY (status, dimensions, counts), so a cached raw
        // verdict replayed under different coverage circumstances gets THIS run's presentation.
        var best = NewsRiskCompletenessDescription.NoRiskWording(
            NewsRiskArchiveCapture.Proven, NewsRiskSearchEnumeration.Complete,
            NewsRiskAssessmentBundle.Complete, 2, 2);
        var degraded = NewsRiskCompletenessDescription.NoRiskWording(
            NewsRiskArchiveCapture.Proven, NewsRiskSearchEnumeration.Truncated,
            NewsRiskAssessmentBundle.Capped, 2, 5);

        Assert.NotEqual(best, degraded);
        Assert.Contains("This is a statement about the 2 supplied article(s)", best);
        Assert.DoesNotContain("incomplete", best);
        Assert.Contains("search enumeration Truncated", degraded);
        Assert.Contains("bundle capped at 2 of 5 qualifying available", degraded);

        // And the rendered markdown for each carries exactly that derived wording.
        var bestMarkdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(Company(
            NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText,
            NewsRiskArchiveCapture.Proven, NewsRiskSearchEnumeration.Complete,
            NewsRiskAssessmentBundle.Complete, 2, 2)));
        var degradedMarkdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(Company(
            NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText,
            NewsRiskArchiveCapture.Proven, NewsRiskSearchEnumeration.Truncated,
            NewsRiskAssessmentBundle.Capped, 2, 5)));
        Assert.Contains(best, bestMarkdown);
        Assert.Contains(degraded, degradedMarkdown);
    }

    [Fact]
    public void CoverageIssues_RenderAsTheDetailList_UnderTheDimensions()
    {
        var company = Company(
            NewsRiskAssessmentStatus.ThesisChallenged,
            NewsRiskArchiveCapture.Unproven,
            NewsRiskSearchEnumeration.Unproven,
            NewsRiskAssessmentBundle.Complete) with
        {
            CoverageIssues = ["archive-batch-unavailable: no batch manifest is readable for this run"],
        };

        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(company));

        Assert.Contains("Coverage issues: archive-batch-unavailable", markdown);
    }

    // ---------------------------------------------------------------------------------------------
    // SPEC 206 §4 — the compact per-row assessment-persistence state.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A recorded marker renders compactly; the failed state is unmistakable and names the id that may not
    /// dereference; a legacy null (not recorded) renders NOTHING — never "durable", because null must never
    /// be interpreted as true, and never a fabricated state on an artifact that predates the contract.
    /// </summary>
    [Fact]
    public void AssessmentPersistenceState_RendersTrueAndFalseDistinctly_AndNullNotAtAll()
    {
        var company = Company(
            NewsRiskAssessmentStatus.ThesisChallenged,
            NewsRiskArchiveCapture.Proven,
            NewsRiskSearchEnumeration.Complete,
            NewsRiskAssessmentBundle.Complete);
        var baseResult = company.ReaderResults[0];

        var durable = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(
            company with { ReaderResults = [baseResult with { DurablyPersisted = true }] }));
        Assert.Contains("Assessment persistence: durable", durable);
        Assert.DoesNotContain("NOT PERSISTED", durable);

        var failed = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(
            company with { ReaderResults = [baseResult with { DurablyPersisted = false }] }));
        Assert.Contains("Assessment persistence: **NOT PERSISTED**", failed);
        Assert.Contains($"`{baseResult.AssessmentId:D}`", failed);
        Assert.Contains("may not dereference", failed);

        // Legacy artifact: the member hydrates null and the row renders exactly as it did before v6.
        var legacy = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(company));
        Assert.DoesNotContain("Assessment persistence", legacy);
    }

    private static void AssertNoAllClear(string markdown)
    {
        // "all clear" in any casing/hyphenation appears NOWHERE (spec 182 §3).
        var flattened = markdown
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("allclear", flattened, StringComparison.OrdinalIgnoreCase);
    }

    private static NewsRiskAssessmentStatus ObsoleteIncompleteCoverage()
    {
#pragma warning disable CS0618 // referenced only to EXCLUDE it — spec 182 retired the status
        return NewsRiskAssessmentStatus.IncompleteCoverage;
#pragma warning restore CS0618
    }
}
