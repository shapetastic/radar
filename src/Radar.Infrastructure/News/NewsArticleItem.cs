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
/// <para>
/// The trailing members are the spec-177 observation payload, ALL defaulted so every pre-177 construction
/// site is unchanged. <see cref="DescriptionRaw"/> is the exact <c>&lt;description&gt;</c> element content the
/// feed supplied, bounded to <see cref="HttpNewsSearchReader.MaxDescriptionUtf8Bytes"/> (UTF-8 BYTES, the
/// canonical bound — never split mid surrogate pair); <see langword="null"/> when the feed supplied none —
/// an absent description is never a headline copied into a second field. <see cref="DescriptionText"/> is
/// its deterministic plain-text rendering (<see cref="HtmlVisibleText"/>); <see cref="DescriptionTruncated"/>
/// says explicitly when the raw payload was cut at the bound (a prefix is never passed off as complete).
/// <see cref="PublisherSiteUrl"/> is the <c>&lt;source url&gt;</c> attribute — publisher-SITE provenance,
/// never a claimed canonical article URL (the article link stays the Google landing <see cref="Url"/>) —
/// kept only when it is an absolute HTTP(S) URL. <see cref="RetrievedAt"/> is the UTC instant the RSS
/// response was retrieved, from the reader's injected <see cref="TimeProvider"/>.
/// </para>
/// </summary>
internal sealed record NewsArticleItem(
    string Url,
    string Title,
    string SourceName,
    DateTimeOffset? PublishedAt,
    string? DescriptionRaw = null,
    string? DescriptionText = null,
    bool DescriptionTruncated = false,
    string? PublisherSiteUrl = null,
    DateTimeOffset RetrievedAt = default);
