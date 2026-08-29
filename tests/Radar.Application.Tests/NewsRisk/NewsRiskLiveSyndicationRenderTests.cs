using Radar.Application.News;
using Radar.Application.NewsRisk;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// SPEC 195 §2 — the pre-collapse syndication measurement survives as recorded provenance.
/// <para>
/// Spec 193 computed <c>SyndicatedDuplicateCount</c>/<c>SyndicatedDistinctPublisherCount</c> onto the
/// TRANSIENT input bundle and no production consumer read either, so after the pass nothing distinguished
/// forty syndicated copies of one story from one article. Carrying a number on an object that is then
/// discarded does not satisfy "record what is discarded".
/// </para>
/// <para>
/// These tests pin the recording and rendering rules: a measured zero is a MEASUREMENT, a hydrated
/// <c>null</c> is NOT RECORDED, and the artifact-level publisher figure is named for what it actually is —
/// a company-publisher incidence sum, never a globally distinct count.
/// </para>
/// <para>
/// The PERSISTED side (a v3 document hydrating null, a v4 document round-tripping the measured values
/// through the real store serializer) lives in
/// <c>Radar.Infrastructure.Tests.NewsRisk.NewsRiskLiveSyndicationPersistenceTests</c>, because the store's
/// <c>RadarFileStoreJson</c> options are internal to Infrastructure and the shape has to be asserted against
/// the options the artifact store actually writes with, not a re-declared copy.
/// </para>
/// </summary>
public sealed class NewsRiskLiveSyndicationRenderTests
{
    private static readonly DateTimeOffset Now = NewsRiskTestData.SelectionAsOf;

    private static NewsRiskLiveCompany Company(
        string name,
        int? syndicatedDuplicates,
        int? syndicatedPublishers) => new(
        CompanyId: Guid.NewGuid(),
        CompanyName: name,
        Ticker: "TST",
        Selections: [new NewsRiskCandidateSelection("default", 1, Guid.NewGuid())],
        Articles:
        [
            new NewsRiskLiveArticle(
                Guid.NewGuid(), "Headline", "Example Wire", "https://example.com/1",
                NewsObservationCaptureMode.ProspectiveRss, "headline"),
        ],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        AssessmentBundle: NewsRiskAssessmentBundle.Complete,
        QualifyingArticleCount: 1,
        CoverageIssues: [],
        ReaderResults: [],
        Judgments: null,
        JudgmentMarker: null,
        SyndicatedDuplicateCount: syndicatedDuplicates,
        SyndicatedDistinctPublisherCount: syndicatedPublishers);

    private static NewsRiskLiveDocument Document(params NewsRiskLiveCompany[] companies) => new(
        SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
        RunId: Guid.NewGuid(),
        SelectionAsOfUtc: Now,
        Caveat: NewsRiskLiveDocument.LiveCaveat,
        Readers: ["reader-a (test-provider:model-a)"],
        Diagnostic: null,
        Companies: companies,
        GeneratedAtUtc: Now);

    /// <summary>
    /// The tag moved BECAUSE the company row gained the measurement (spec 195 §2), and again because the
    /// materialization block gained the join measurement (spec 197 §1.2).
    /// </summary>
    [Fact]
    public void SchemaVersion_IsV5()
    {
        Assert.Equal("news-risk-live-v5", NewsRiskLiveDocument.CurrentSchemaVersion);
    }

    /// <summary>A company that syndicated renders its compact line, naming both measured numbers.</summary>
    [Fact]
    public void CompanyWithSyndication_RendersTheCompactLine()
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(Company("Test Co", 3, 4)));

        Assert.Contains("Syndication before collapse: 3 duplicate cop", markdown, StringComparison.Ordinal);
        Assert.Contains("across 4 distinct publisher(s)", markdown, StringComparison.Ordinal);

        // Provenance, never an input — stated in the rendered text, not just in a code comment.
        Assert.Contains(
            "not a scoring, cohort, cache or model input", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 201 §4: a recorded duplicate count beside a NULL publisher count renders "not recorded" for the
    /// publishers — never "across 0 distinct publisher(s)", which would print a measurement that was not
    /// taken. Benign today (both construction sites set the pair together); pinned so it stays honest if
    /// they ever diverge.
    /// </summary>
    [Fact]
    public void CompanyWithDuplicatesButNoPublisherCount_RendersNotRecorded_NeverZero()
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(Company("Test Co", 3, null)));

        Assert.Contains("Syndication before collapse: 3 duplicate cop", markdown, StringComparison.Ordinal);
        Assert.Contains("a not-recorded number of distinct publishers", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("across 0 distinct publisher", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MEASURED ZERO stays a zero in the document (that is where the honest zero lives) but does not
    /// print a per-company line — repeating "0 duplicate copies" under every company would bury the
    /// companies that actually syndicated.
    /// </summary>
    [Fact]
    public void CompanyWithAMeasuredZero_IsRecordedButRendersNoPerCompanyLine()
    {
        var company = Company("Test Co", 0, 0);

        Assert.Equal(0, company.SyndicatedDuplicateCount);
        Assert.Equal(0, company.SyndicatedDistinctPublisherCount);

        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(company));
        Assert.DoesNotContain("Syndication before collapse:", markdown, StringComparison.Ordinal);

        // …but the artifact-level totals DO render, because a measurement was taken.
        Assert.Contains(
            "## Syndication before collapse (current-run enumeration provenance)",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("Collapsed copies: 0 across 0 of 1 company", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The artifact-level totals, and the naming rule that matters: the publisher figure is a
    /// COMPANY-PUBLISHER INCIDENCE SUM (a publisher counted once per company, summed across companies) and
    /// the artifact says so. Only per-company COUNTS reach the renderer — the publisher NAMES are not on the
    /// document — so a globally distinct figure is not computable here, and labelling a sum "distinct" would
    /// be a false label on a real number.
    /// </summary>
    [Fact]
    public void ArtifactTotals_NameThePublisherFigureAsAnIncidenceSum_NotAsGloballyDistinct()
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(
            Company("Alpha", 3, 4),
            Company("Beta", 2, 3),
            Company("Gamma", 0, 0)));

        Assert.Contains("Collapsed copies: 5 across 2 of 3 company", markdown, StringComparison.Ordinal);
        Assert.Contains("Company-publisher incidence sum: 7", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "This is NOT a globally distinct publisher count", markdown, StringComparison.Ordinal);
        Assert.Contains("ONCE PER COMPANY", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// No direction is read into syndication (spec 195 §5): widely-carried news is neither good nor bad
    /// news, and the artifact must not imply otherwise. Also the hard output rule.
    /// </summary>
    [Fact]
    public void SyndicationRendering_IsAdviceFreeAndDirectionFree()
    {
        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(Document(Company("Test Co", 40, 12)));

        foreach (var banned in new[] { "buy", "sell", "guaranteed", "safe bet", "upside" })
        {
            Assert.DoesNotContain(banned, markdown, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("neither good news nor bad news", markdown, StringComparison.Ordinal);
    }
}
