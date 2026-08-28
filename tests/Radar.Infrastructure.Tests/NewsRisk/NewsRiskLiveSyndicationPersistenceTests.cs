using System.Text.Json;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// SPEC 195 §2, PERSISTED side: the live document's syndication measurement round-trips through the EXACT
/// serializer options <c>FileNewsRiskArtifactStore</c> writes with (<see cref="RadarFileStoreJson"/>), so
/// the JSON shape is asserted against production rather than a re-declared copy of the options.
/// <para>
/// The two facts that matter are DIFFERENT facts: an accrued <c>news-risk-live-v3</c> document hydrates
/// both members as <c>null</c> = NOT RECORDED, while a v4 run always writes measured integers including an
/// honest zero. Nothing historical is rewritten or backfilled.
/// </para>
/// </summary>
public sealed class NewsRiskLiveSyndicationPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An accrued v3 document — no syndication members at all — hydrates BOTH fields as <c>null</c>,
    /// never a fabricated zero, and renders exactly as it did before spec 195: no per-company line and no
    /// totals section.
    /// </summary>
    [Fact]
    public void AccruedV3Document_HydratesBothFieldsAsNull_AndRendersNoSyndicationSection()
    {
        const string V3Json = """
        {
          "schemaVersion": "news-risk-live-v3",
          "runId": "11111111-1111-1111-1111-111111111111",
          "selectionAsOfUtc": "2026-08-26T12:00:00+00:00",
          "caveat": "caveat",
          "readers": ["reader-a (test-provider:model-a)"],
          "diagnostic": null,
          "companies": [
            {
              "companyId": "22222222-2222-2222-2222-222222222222",
              "companyName": "Test Co",
              "ticker": "TST",
              "selections": [],
              "articles": [],
              "archiveCapture": "Proven",
              "searchEnumeration": "Complete",
              "assessmentBundle": "Complete",
              "qualifyingArticleCount": 1,
              "coverageIssues": [],
              "readerResults": []
            }
          ],
          "generatedAtUtc": "2026-08-26T12:00:00+00:00"
        }
        """;

        var document = JsonSerializer.Deserialize<NewsRiskLiveDocument>(V3Json, RadarFileStoreJson.Options);

        Assert.NotNull(document);
        Assert.Equal("news-risk-live-v3", document!.SchemaVersion);

        var company = Assert.Single(document.Companies);
        Assert.Null(company.SyndicatedDuplicateCount);
        Assert.Null(company.SyndicatedDistinctPublisherCount);

        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(document);
        Assert.DoesNotContain("Syndication before collapse", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// A current-schema document carries the measured values in the JSON — including an honest ZERO, which
    /// is a measurement and must not be omitted into indistinguishability from "not recorded".
    /// </summary>
    [Theory]
    [InlineData(3, 4)]
    [InlineData(0, 0)]
    public void CurrentDocument_AlwaysWritesTheMeasuredValues_AndRoundTripsThem(
        int duplicates, int publishers)
    {
        var document = Document(Company(duplicates, publishers));

        var json = JsonSerializer.Serialize(document, RadarFileStoreJson.Options);

        // Present in the persisted text, not merely reachable on the in-memory object.
        Assert.Contains("\"syndicatedDuplicateCount\":", json, StringComparison.Ordinal);
        Assert.Contains("\"syndicatedDistinctPublisherCount\":", json, StringComparison.Ordinal);

        var round = JsonSerializer.Deserialize<NewsRiskLiveDocument>(json, RadarFileStoreJson.Options);
        var company = Assert.Single(round!.Companies);

        Assert.Equal(duplicates, company.SyndicatedDuplicateCount);
        Assert.Equal(publishers, company.SyndicatedDistinctPublisherCount);
        Assert.Equal("news-risk-live-v5", round.SchemaVersion);
    }

    private static NewsRiskLiveCompany Company(int? duplicates, int? publishers) => new(
        CompanyId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        CompanyName: "Test Co",
        Ticker: "TST",
        Selections: [],
        Articles: [],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        AssessmentBundle: NewsRiskAssessmentBundle.Complete,
        QualifyingArticleCount: 1,
        CoverageIssues: [],
        ReaderResults: [],
        Judgments: null,
        JudgmentMarker: null,
        SyndicatedDuplicateCount: duplicates,
        SyndicatedDistinctPublisherCount: publishers);

    private static NewsRiskLiveDocument Document(params NewsRiskLiveCompany[] companies) => new(
        SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
        RunId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SelectionAsOfUtc: Now,
        Caveat: NewsRiskLiveDocument.LiveCaveat,
        Readers: ["reader-a (test-provider:model-a)"],
        Diagnostic: null,
        Companies: companies,
        GeneratedAtUtc: Now);
}
