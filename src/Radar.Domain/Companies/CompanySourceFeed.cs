namespace Radar.Domain.Companies;

/// <summary>
/// A configured collection source bound to one watched company (e.g. an RSS investor-news feed).
/// Collectors read these to know what to fetch; the bound CompanyId is the high-confidence company
/// hint for evidence from this feed (slice 30).
/// </summary>
public sealed record CompanySourceFeed(
    Guid Id,
    Guid CompanyId,
    string FeedType,     // e.g. "rss"
    string Name,
    string Url,
    DateTimeOffset CreatedAtUtc);
