using Radar.Domain.Companies;

namespace Radar.Application.Collectors;

/// <summary>
/// The watch universe Radar hands every collector at collection time. A company-specific collector
/// uses <see cref="Companies"/> for company-hint resolution and <see cref="SourceFeeds"/> to know
/// which feeds to fetch (the bound CompanyId is the high-confidence company hint). Kept a record so
/// later slices can extend it without breaking callers.
/// </summary>
public sealed record CollectionContext(
    IReadOnlyList<Company> Companies,
    IReadOnlyList<CompanySourceFeed> SourceFeeds)
{
    /// <summary>
    /// Convenience constructor for callers/tests that only supply companies; source feeds default to
    /// empty.
    /// </summary>
    public CollectionContext(IReadOnlyList<Company> companies) : this(companies, []) { }

    /// <summary>
    /// The configured <see cref="CompanySourceFeed"/>s whose <c>FeedType</c> matches <paramref name="feedType"/>
    /// (case-insensitive), in the canonical deterministic order (by CompanyId, then feed Id). Each collector
    /// calls this with its own kind (e.g. "rss", "sec") to get exactly the feeds it should fetch; the feed's
    /// <c>Url</c> carries that collector's per-company input (a feed URL, or an API endpoint / identifier).
    /// Returns an empty list when no feed matches. Provenance: the bound CompanyId remains the high-confidence
    /// company hint for evidence from that feed.
    /// </summary>
    public IReadOnlyList<CompanySourceFeed> FeedsOfType(string feedType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedType);
        return SourceFeeds
            .Where(f => string.Equals(f.FeedType, feedType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.CompanyId)
            .ThenBy(f => f.Id)
            .ToList();
    }
}
