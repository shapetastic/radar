namespace Radar.Application.News;

/// <summary>
/// One immutable point-in-time news observation (spec 177 §4) — the archive's unit of record. It preserves
/// exactly what Radar observed about one article, when it observed it, with enough provider payload to
/// replay a later semantic read without re-fetching anything.
/// <para>
/// It is deliberately NOT evidence: the archive is never read by extraction, resolution, review or scoring,
/// is excluded from <c>ScoringConfigVersion</c>, strategy identity and collection provenance, and shares no
/// identity with <c>EvidenceItem</c> (spec 145's content-derived evidence ids are untouched).
/// </para>
/// <para>
/// <see cref="FirstObservedAtUtc"/> is IMMUTABLE and drives the on-disk year/month partition; cross-partition
/// dedupe through the hydrated id index preserves the original earliest value (a later re-observation of the
/// same payload never writes a second file and never moves the instant).
/// </para>
/// </summary>
public sealed record NewsObservationRecord(
    string SchemaVersion,
    Guid ObservationId,
    Guid? CompanyId,
    string? Ticker,
    string? Collector,
    string? QueryPhrase,
    Guid? FeedId,
    string? FeedName,
    string GoogleLandingUrl,
    string Publisher,
    string? PublisherSiteUrl,
    string Headline,
    string? DescriptionRaw,
    string? DescriptionText,
    bool DescriptionTruncated,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset RetrievedAtUtc,
    DateTimeOffset FirstObservedAtUtc,
    string PayloadHash,
    NewsObservationCaptureMode CaptureMode,
    NewsArticleFetchResult? ArticleFetch)
{
    /// <summary>The archive schema version stamped on every record and batch manifest.</summary>
    public const string CurrentSchemaVersion = "news-observation-v1";

    /// <summary>
    /// Mints the <see cref="NewsObservationCaptureMode.ProspectiveRss"/> record for one collector-supplied
    /// candidate: identity is derived here (the ONE identity definition,
    /// <see cref="NewsObservationIdentity"/>), and <see cref="FirstObservedAtUtc"/> is the candidate's real
    /// retrieval instant — the moment Radar first knew this payload existed.
    /// </summary>
    public static NewsObservationRecord Prospective(NewsObservationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var payloadHash = NewsObservationIdentity.ComputePayloadHash(
            NewsObservationCaptureMode.ProspectiveRss,
            candidate.GoogleLandingUrl,
            candidate.Headline,
            candidate.Publisher,
            candidate.DescriptionRaw);

        return new NewsObservationRecord(
            SchemaVersion: CurrentSchemaVersion,
            ObservationId: NewsObservationIdentity.ObservationIdFor(candidate.GoogleLandingUrl, payloadHash),
            CompanyId: candidate.CompanyId,
            Ticker: candidate.Ticker,
            Collector: candidate.Collector,
            QueryPhrase: candidate.QueryPhrase,
            FeedId: candidate.FeedId,
            FeedName: candidate.FeedName,
            GoogleLandingUrl: candidate.GoogleLandingUrl,
            Publisher: candidate.Publisher,
            PublisherSiteUrl: candidate.PublisherSiteUrl,
            Headline: candidate.Headline,
            DescriptionRaw: candidate.DescriptionRaw,
            DescriptionText: candidate.DescriptionText,
            DescriptionTruncated: candidate.DescriptionTruncated,
            PublishedAtUtc: candidate.PublishedAtUtc,
            RetrievedAtUtc: candidate.RetrievedAtUtc,
            FirstObservedAtUtc: candidate.RetrievedAtUtc,
            PayloadHash: payloadHash,
            CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
            ArticleFetch: null);
    }
}
