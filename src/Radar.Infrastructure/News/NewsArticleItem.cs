namespace Radar.Infrastructure.News;

/// <summary>
/// A single parsed news article from a Google News RSS search response (one record per <c>&lt;item&gt;</c>).
/// Raw metadata only — spec 81's collector will synthesize evidence Title/RawText from these real fields and
/// never fabricate article body text (a news SEARCH returns headlines only, not full text). <see cref="Url"/>
/// is the stable <c>news.google.com/rss/articles/…</c> landing page used for provenance and within-feed
/// dedupe; rows missing it are skipped by the reader (unattributable/undedupable). <see cref="Title"/> is the
/// FULL headline as-is (Google News appends <c>" - &lt;Publisher&gt;"</c>) — kept intact for provenance rather
/// than stripped. <see cref="SourceName"/> is the third-party outlet name (the distinct source name that lifts
/// <c>AttentionScore</c>), taken from the item's <c>&lt;source&gt;</c> element, falling back to the
/// <c>" - Publisher"</c> title suffix, then the empty string. <see cref="PublishedAt"/> is the item's
/// <c>&lt;pubDate&gt;</c> (RFC 1123) parsed to a UTC instant; <see langword="null"/> when absent/unparseable.
/// </summary>
internal sealed record NewsArticleItem(
    string Url,
    string Title,
    string SourceName,
    DateTimeOffset? PublishedAt);
