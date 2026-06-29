namespace Radar.Infrastructure.Rss;

/// <summary>
/// A single parsed RSS/Atom item. Raw and un-normalized; the collector maps it to a
/// <see cref="Radar.Application.Collectors.CollectedEvidence"/> and the mapper normalizes later.
/// <see cref="Content"/> is the full item body when the feed supplies it (RSS
/// <c>content:encoded</c> or Atom <c>content</c>); <see cref="Summary"/> is the teaser. Both stay
/// raw/un-normalized.
/// </summary>
internal sealed record RssFeedItem(
    string? Id,
    string Title,
    string? Summary,
    string? Link,
    DateTimeOffset? PublishedAt,
    string? Content = null);
