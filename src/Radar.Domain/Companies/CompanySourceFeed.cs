namespace Radar.Domain.Companies;

/// <summary>
/// A configured collection source bound to one watched company (e.g. an RSS investor-news feed).
/// Collectors read these to know what to fetch; the bound CompanyId is the high-confidence company
/// hint for evidence from this feed (slice 30).
/// <para>
/// Feed-type seam: <c>FeedType</c> is the collector-kind discriminator (<c>"rss"</c>, and later
/// <c>"sec"</c>, <c>"govcontract"</c>, …). A collector selects only its own feeds via
/// <c>CollectionContext.FeedsOfType(kind)</c>. <c>Url</c> carries that collector's per-company input:
/// a feed URL, or an API endpoint / identifier such as a CIK-based SEC URL.
/// </para>
/// </summary>
public sealed record CompanySourceFeed(
    Guid Id,
    Guid CompanyId,
    string FeedType,     // e.g. "rss"
    string Name,
    string Url,
    DateTimeOffset CreatedAtUtc);
