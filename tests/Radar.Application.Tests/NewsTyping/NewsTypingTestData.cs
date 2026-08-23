using Radar.Application.News;
using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>Shared deterministic builders for the spec-181 news-typing tests.</summary>
internal static class NewsTypingTestData
{
    public static readonly DateTimeOffset AsOf = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    public const string CohortKey =
        "openai:test-model|news-typing-prompt-v1|news-typing-schema-v1|news-event-taxonomy-v1";

    public static NewsTypingInputObservation Input(
        string headline = "Test Co widens quarterly loss to $5 million",
        string? description = "The company reported a wider loss and shares fell 11.8% in trading.",
        string? body = null,
        Guid? observationId = null,
        Guid? companyId = null,
        string? ticker = "TST",
        string publisher = "Example Wire",
        NewsObservationCaptureMode captureMode = NewsObservationCaptureMode.ProspectiveRss,
        DateTimeOffset? firstObservedAtUtc = null,
        string payloadHash = "ph-1") => new(
        ObservationId: observationId ?? new Guid("11111111-1111-1111-1111-111111111111"),
        Headline: headline,
        DescriptionText: description,
        BodyText: body,
        Publisher: publisher,
        CaptureMode: captureMode,
        PayloadHash: payloadHash,
        FirstObservedAtUtc: firstObservedAtUtc ?? AsOf.AddDays(-1),
        CompanyId: companyId,
        Ticker: ticker);

    public static NewsTypingModelFact Fact(
        string[]? eventTypes = null,
        string? statement = "Test Co widened its quarterly loss to $5 million",
        string? temporalScope = "Q2",
        string? attribution = "publisher",
        string? assertionStatus = "reported",
        double? confidence = 0.9,
        string[]? citations = null) => new(
        EventTypes: eventTypes ?? ["EarningsOrGuidance"],
        Statement: statement,
        TemporalScope: temporalScope,
        Attribution: attribution,
        AssertionStatus: assertionStatus,
        Confidence: confidence,
        Citations: citations ?? ["widens quarterly loss to $5 million"]);

    public static FactFamilyInputFact FamilyFact(
        Guid factId,
        Guid companyId,
        string statement,
        DateTimeOffset observedAtUtc,
        string publisher = "Example Wire",
        NewsEventType[]? eventTypes = null,
        NewsObservationCaptureMode captureMode = NewsObservationCaptureMode.ProspectiveRss) => new(
        FactId: factId,
        CompanyId: companyId,
        EventTypes: eventTypes ?? [NewsEventType.RegulatoryOrLegal],
        Statement: statement,
        FirstObservedAtUtc: observedAtUtc,
        Publisher: publisher,
        ObservationId: Guid.NewGuid(),
        CaptureMode: captureMode);
}
