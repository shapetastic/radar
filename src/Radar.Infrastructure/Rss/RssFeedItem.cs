namespace Radar.Infrastructure.Rss;

/// <summary>
/// A single parsed RSS/Atom item. Raw and un-normalized; the collector maps it to a
/// <see cref="Radar.Application.Collectors.CollectedEvidence"/> and the mapper normalizes later.
/// </summary>
internal sealed record RssFeedItem(
    string? Id,
    string Title,
    string? Summary,
    string? Link,
    DateTimeOffset? PublishedAt);
