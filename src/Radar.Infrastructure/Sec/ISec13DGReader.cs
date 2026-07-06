namespace Radar.Infrastructure.Sec;

/// <summary>
/// Infrastructure-internal abstraction over the SEC Schedule 13D/13G submissions fetch + form-classify, so
/// the collector is fully offline-testable (tests supply fixture filings; the real reader uses
/// <c>HttpClient</c> + <c>System.Text.Json</c>). Unlike the Form 4 reader there is NO per-filing body fetch
/// (v1 is metadata-only). A flaky or delisted issuer reports its failure mode via the returned
/// <see cref="Sec13DGReadResult"/> rather than swallowing it; caller-requested cancellation still throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal interface ISec13DGReader
{
    Task<Sec13DGReadResult> ReadAsync(string submissionsUrl, CancellationToken ct);
}
