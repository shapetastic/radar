namespace Radar.Infrastructure.Sec;

/// <summary>
/// Infrastructure-internal abstraction over the SEC Form 4 submissions fetch + per-filing ownership-XML
/// fetch/parse/classify, so the collector is fully offline-testable (tests supply fixture filings; the real
/// reader uses <c>HttpClient</c> + <c>System.Text.Json</c> + <c>XDocument</c>). A flaky or delisted issuer
/// reports its failure mode via the returned <see cref="SecForm4ReadResult"/> rather than swallowing it; a
/// single bad Form 4 is skipped without failing the whole feed; caller-requested cancellation still throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal interface ISecForm4Reader
{
    Task<SecForm4ReadResult> ReadAsync(string submissionsUrl, CancellationToken ct);
}
