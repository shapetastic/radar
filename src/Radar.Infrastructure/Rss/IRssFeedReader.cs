namespace Radar.Infrastructure.Rss;

/// <summary>
/// Infrastructure-internal abstraction over RSS fetch + parse so the collector is fully
/// offline-testable (tests supply fixture items; the real reader uses <c>HttpClient</c> +
/// <c>SyndicationFeed</c>). A flaky feed degrades to an empty list rather than throwing.
/// </summary>
internal interface IRssFeedReader
{
    Task<IReadOnlyList<RssFeedItem>> ReadAsync(string feedUrl, CancellationToken ct);
}
