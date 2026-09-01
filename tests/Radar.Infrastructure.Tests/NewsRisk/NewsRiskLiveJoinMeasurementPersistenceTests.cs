using System.Text.Json;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// SPEC 197 §1.2, PERSISTED side: the run-level observation→evidence JOIN measurement round-trips through
/// the EXACT serializer options <c>FileNewsRiskArtifactStore</c> writes with
/// (<see cref="RadarFileStoreJson"/>), so the JSON shape is asserted against production rather than a
/// re-declared copy of the options.
/// <para>
/// Two DIFFERENT facts, and the schema tag moved so a reader can tell them apart: an accrued
/// <c>news-risk-live-v4</c> document carries a materialization summary with NO join measurement, which
/// hydrates as <c>null</c> = NOT RECORDED; every v5 run that attempted the join writes the measured
/// buckets, including an honest zero. Nothing historical is rewritten or backfilled.
/// </para>
/// </summary>
public sealed class NewsRiskLiveJoinMeasurementPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AccruedV4Document_HydratesTheJoinMeasurementAsNull_NeverAsMeasuredZeros()
    {
        const string V4Json = """
        {
          "schemaVersion": "news-risk-live-v4",
          "runId": "11111111-1111-1111-1111-111111111111",
          "selectionAsOfUtc": "2026-08-26T12:00:00+00:00",
          "caveat": "caveat",
          "readers": ["reader-a (test-provider:model-a)"],
          "diagnostic": null,
          "companies": [],
          "generatedAtUtc": "2026-08-26T12:00:00+00:00",
          "signalMaterialization": {
            "judgmentsConsidered": 9,
            "eligible": 9,
            "materialized": 2,
            "alreadyMaterialized": 0,
            "validationRejected": 0,
            "writeFailed": 0,
            "skips": { "UnresolvedObservation": 7 }
          }
        }
        """;

        var document = JsonSerializer.Deserialize<NewsRiskLiveDocument>(V4Json, RadarFileStoreJson.Options);

        Assert.NotNull(document);
        Assert.Equal("news-risk-live-v4", document!.SchemaVersion);

        var summary = document.SignalMaterialization;
        Assert.NotNull(summary);

        // NOT RECORDED — never an all-zero measurement, which would claim the run measured a join it never
        // performed.
        Assert.Null(summary!.JoinCounts);

        // The pre-197 generic skip token still deserializes: the vocabulary member is retained precisely so
        // an accrued artifact does not become unreadable (the spec-189 `Failed` precedent).
        Assert.Equal(7, summary.SkipCount(NewsJudgmentSignalSkipReason.UnresolvedObservation));

        // …and the spec-197 counter that did not exist then reads as its documented default.
        Assert.Equal(0, summary.PriorVersionOccupied);

        var markdown = NewsRiskLiveArtifactRenderer.RenderMarkdown(document);
        Assert.Contains("Not attempted this run", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Observations: 0", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 2, 1, 4, 5)]
    [InlineData(0, 0, 0, 0, 0)]
    public void ACurrentDocument_AlwaysWritesTheMeasuredBuckets_AndRoundTripsThem(
        int exactInstant, int exactUrl, int fallback, int noMatch, int ambiguous)
    {
        var counts = new NewsObservationEvidenceJoinCounts(
            exactInstant, exactUrl, fallback, noMatch, ambiguous);
        var document = Document(new NewsJudgmentSignalMaterializationSummary(
            JudgmentsConsidered: 9,
            Eligible: 4,
            Materialized: 2,
            AlreadyMaterialized: 1,
            ValidationRejected: 0,
            WriteFailed: 0,
            Skips: new Dictionary<NewsJudgmentSignalSkipReason, int>
            {
                [NewsJudgmentSignalSkipReason.ObservationAmbiguous] = 1,
            },
            PriorVersionOccupied: 1,
            JoinCounts: counts));

        var json = JsonSerializer.Serialize(document, RadarFileStoreJson.Options);

        // Present in the persisted TEXT, not merely reachable on the in-memory object — an honest zero must
        // be written rather than omitted into indistinguishability from "not recorded".
        Assert.Contains("\"joinCounts\":", json, StringComparison.Ordinal);
        Assert.Contains("\"exactArticleInstant\":", json, StringComparison.Ordinal);
        Assert.Contains("\"priorVersionOccupied\":", json, StringComparison.Ordinal);

        var round = JsonSerializer.Deserialize<NewsRiskLiveDocument>(json, RadarFileStoreJson.Options);

        Assert.Equal("news-risk-live-v6", round!.SchemaVersion);
        Assert.Equal(counts, round.SignalMaterialization!.JoinCounts);
        Assert.Equal(1, round.SignalMaterialization.PriorVersionOccupied);
        Assert.Equal(
            1, round.SignalMaterialization.SkipCount(NewsJudgmentSignalSkipReason.ObservationAmbiguous));
    }

    private static NewsRiskLiveDocument Document(
        NewsJudgmentSignalMaterializationSummary summary) => new(
        SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
        RunId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SelectionAsOfUtc: Now,
        Caveat: NewsRiskLiveDocument.LiveCaveat,
        Readers: ["reader-a (test-provider:model-a)"],
        Diagnostic: null,
        Companies: [],
        GeneratedAtUtc: Now,
        SignalMaterialization: summary);
}
