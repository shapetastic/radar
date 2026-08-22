using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Domain.Scoring;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>Shared deterministic builders for the spec-179 news-risk tests.</summary>
internal static class NewsRiskTestData
{
    public static readonly DateTimeOffset SelectionAsOf =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    public static CompanyScoreSnapshot Snapshot(Guid companyId, int opportunity = 50) => new(
        Id: Guid.NewGuid(),
        CompanyId: companyId,
        ScoringVersion: "radar-formula-v8",
        TrajectoryScore: 50,
        OpportunityScore: opportunity,
        AttentionScore: 10,
        EvidenceConfidenceScore: 40,
        SignalVelocityScore: 20,
        Explanation: "test",
        ComponentJson: "{}",
        WindowStartUtc: SelectionAsOf.AddDays(-30),
        WindowEndUtc: SelectionAsOf,
        CreatedAtUtc: SelectionAsOf,
        ScoringConfigVersion: "radar-scoring-fp-test",
        StrategyName: null,
        CollectionProvenance: null);

    public static StrategyReportRow Row(
        int rank, Guid companyId, string name, string? ticker = null) => new(
        Rank: rank,
        CompanyId: companyId,
        CompanyName: name,
        Ticker: ticker,
        ScoreSnapshotId: Guid.NewGuid(),
        Snapshot: Snapshot(companyId));

    public static StrategyReportSection Section(
        string strategyName,
        bool isPrimary,
        StrategyPurpose purpose,
        params StrategyReportRow[] rows) => new(
        StrategyName: strategyName,
        FormulaVersion: "radar-formula-v8",
        ScoringConfigVersion: "radar-scoring-fp-test",
        IsPrimary: isPrimary,
        CompaniesScored: rows.Length,
        CompaniesWithLinkedEvidence: rows.Length,
        Rows: rows)
    {
        Purpose = purpose,
    };

    public static NewsObservationRecord Observation(
        Guid companyId,
        string headline,
        DateTimeOffset observedAtUtc,
        string? description = "desc",
        DateTimeOffset? publishedAtUtc = null,
        DateTimeOffset? retrievedAtUtc = null,
        string publisher = "Example Wire",
        NewsObservationCaptureMode captureMode = NewsObservationCaptureMode.ProspectiveRss,
        NewsArticleFetchResult? articleFetch = null,
        Guid? observationId = null) => new(
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        ObservationId: observationId ?? Guid.NewGuid(),
        CompanyId: companyId,
        Ticker: "TST",
        Collector: "newssearch",
        QueryPhrase: "Test Co",
        FeedId: null,
        FeedName: "newssearch: Test Co",
        GoogleLandingUrl: "https://news.google.com/articles/" + Guid.NewGuid().ToString("N"),
        Publisher: publisher,
        PublisherSiteUrl: null,
        Headline: headline,
        DescriptionRaw: description,
        DescriptionText: description,
        DescriptionTruncated: false,
        PublishedAtUtc: publishedAtUtc,
        RetrievedAtUtc: retrievedAtUtc ?? observedAtUtc,
        FirstObservedAtUtc: observedAtUtc,
        PayloadHash: "hash-" + headline.GetHashCode(StringComparison.Ordinal).ToString("x8"),
        CaptureMode: captureMode,
        ArticleFetch: articleFetch);

    public static NewsRiskInputArticle Article(
        Guid observationId,
        string headline,
        string? description = null,
        string? body = null) => new(
        ObservationId: observationId,
        Headline: headline,
        DescriptionText: description,
        BodyText: body,
        Publisher: "Example Wire",
        Url: "https://example.com/a",
        PublishedAtUtc: null,
        RetrievedAtUtc: SelectionAsOf,
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        PayloadHash: "ph",
        BodyContentHash: body is null ? null : "bh",
        BodyRetrievedAtUtc: body is null ? null : SelectionAsOf,
        BodyExtractorVersion: null,
        BodyRetrievalPolicy: null);
}
