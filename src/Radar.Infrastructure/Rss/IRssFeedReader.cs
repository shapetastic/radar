namespace Radar.Infrastructure.Rss;

/// <summary>
/// Infrastructure-internal abstraction over RSS fetch + parse so the collector is fully
/// offline-testable (tests supply fixture items; the real reader uses <c>HttpClient</c> +
/// <c>SyndicationFeed</c>). A flaky feed reports its failure mode via the returned
/// <see cref="RssFeedReadResult"/> rather than swallowing it; caller-requested cancellation still
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface IRssFeedReader
{
    Task<RssFeedReadResult> ReadAsync(string feedUrl, CancellationToken ct);
}
