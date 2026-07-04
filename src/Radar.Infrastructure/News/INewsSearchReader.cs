namespace Radar.Infrastructure.News;

/// <summary>
/// Infrastructure-internal abstraction over the Google News RSS search GET + parse so spec 81's collector is
/// fully offline-testable (tests supply fixture RSS; the real reader uses <c>HttpClient</c> +
/// <c>System.Xml.Linq</c>). A company with no recent coverage, an unreachable endpoint, the request's own
/// timeout, malformed XML, and an HTTP 429 rate-limit each report their mode via the returned
/// <see cref="NewsSearchReadResult"/> rather than swallowing it; caller-requested cancellation still throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal interface INewsSearchReader
{
    Task<NewsSearchReadResult> ReadAsync(NewsSearchQuery query, CancellationToken ct);
}
