namespace Radar.Infrastructure.Sec;

/// <summary>
/// Infrastructure-internal abstraction over the SEC EDGAR submissions fetch + parse so the collector
/// is fully offline-testable (tests supply fixture filings; the real reader uses <c>HttpClient</c> +
/// <c>System.Text.Json</c>). A flaky or delisted issuer reports its failure mode via the returned
/// <see cref="SecFilingReadResult"/> rather than swallowing it; caller-requested cancellation still
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface ISecFilingReader
{
    Task<SecFilingReadResult> ReadAsync(string submissionsUrl, CancellationToken ct);
}
