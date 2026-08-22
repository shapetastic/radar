namespace Radar.Application.News;

/// <summary>
/// One relevance-kept news-search article as the collector SAW it — the observational sidecar row a
/// collector attaches to its <see cref="Radar.Application.Collectors.CollectionResult"/> (spec 177 §3,
/// the same trailing-optional compatibility shape as spec 169's <c>CompanyCoverage</c>).
/// <para>
/// It carries the raw bounded provider fields and provenance only; identity (payload hash / observation id)
/// and the immutable <c>FirstObservedAtUtc</c> are archive concerns, minted by
/// <see cref="NewsObservationRecord.Prospective"/> in the collection orchestration. The collector itself
/// never touches a filesystem store.
/// </para>
/// <para>
/// Only SURVIVING articles become candidates — post relevance filter, post within-feed URL dedupe, post
/// per-feed cap — so an off-topic search result is never archived against the company. Evidence dedupe and
/// observation capture answer different questions: a candidate is emitted even when the corresponding
/// evidence turns out to be an accrued <c>AddIfNewAsync</c> duplicate.
/// </para>
/// </summary>
/// <param name="CompanyId">The company the feed is bound to.</param>
/// <param name="Ticker">The company's ticker when known (seed ticker, falling back to the feed token's ticker).</param>
/// <param name="Collector">The producing collector's stable provenance name (e.g. <c>newssearch</c>).</param>
/// <param name="QueryPhrase">The query phrase the feed searched.</param>
/// <param name="FeedId">The configured source feed's id.</param>
/// <param name="FeedName">The configured source feed's display name.</param>
/// <param name="GoogleLandingUrl">The Google News landing URL (<c>&lt;link&gt;</c>), exactly as returned.</param>
/// <param name="Publisher">The third-party outlet name (may be empty when unattributable).</param>
/// <param name="PublisherSiteUrl">The <c>&lt;source url&gt;</c> publisher-site provenance, when an absolute HTTP(S) URL.</param>
/// <param name="Headline">The full headline as supplied (including any <c>" - Publisher"</c> suffix).</param>
/// <param name="DescriptionRaw">The exact bounded <c>&lt;description&gt;</c> payload; <c>null</c> when absent.</param>
/// <param name="DescriptionText">Deterministic plain-text rendering of the description; <c>null</c> when absent/empty.</param>
/// <param name="DescriptionTruncated">Whether <paramref name="DescriptionRaw"/> was cut at the bound.</param>
/// <param name="PublishedAtUtc">The item's parsed <c>pubDate</c>, when present.</param>
/// <param name="RetrievedAtUtc">The UTC instant the RSS response carrying this item was retrieved.</param>
public sealed record NewsObservationCandidate(
    Guid CompanyId,
    string? Ticker,
    string Collector,
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
    DateTimeOffset RetrievedAtUtc);
