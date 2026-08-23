using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// One observation as supplied to the typing extractor (spec 181 §2/§4): exactly the citable fields —
/// headline, the archived RSS description when present, and the archived permitted publisher body when
/// present (a stored <c>Fetched</c> result; NOTHING is ever fetched by the typing pass) — plus the
/// provenance the typing record and the family builder need. Publisher/timestamps ride along as provenance,
/// never as model-citable text.
/// </summary>
public sealed record NewsTypingInputObservation(
    Guid ObservationId,
    string Headline,
    string? DescriptionText,
    string? BodyText,
    string Publisher,
    NewsObservationCaptureMode CaptureMode,
    string PayloadHash,
    DateTimeOffset FirstObservedAtUtc,
    Guid? CompanyId,
    string? Ticker)
{
    /// <summary>Whether any citable text was supplied at all (a blank union is never sent to a model).</summary>
    public bool HasSuppliedText =>
        !string.IsNullOrWhiteSpace(Headline)
        || !string.IsNullOrWhiteSpace(DescriptionText)
        || !string.IsNullOrWhiteSpace(BodyText);

    /// <summary>
    /// Builds the extractor input from an archived record — the ONE projection rule, shared by the generator
    /// and the tests. The archived body is attached ONLY for a stored <c>Fetched</c> outcome (spec 177 §6);
    /// the typing pass never fetches anything new.
    /// </summary>
    public static NewsTypingInputObservation FromRecord(NewsObservationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var body = record.ArticleFetch is { Outcome: NewsArticleFetchOutcome.Fetched, BodyText: not null } fetch
            ? fetch.BodyText
            : null;
        return new NewsTypingInputObservation(
            ObservationId: record.ObservationId,
            Headline: record.Headline,
            DescriptionText: record.DescriptionText,
            BodyText: body,
            Publisher: record.Publisher,
            CaptureMode: record.CaptureMode,
            PayloadHash: record.PayloadHash,
            FirstObservedAtUtc: record.FirstObservedAtUtc,
            CompanyId: record.CompanyId,
            Ticker: record.Ticker);
    }
}
