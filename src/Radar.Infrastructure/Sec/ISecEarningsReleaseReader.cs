namespace Radar.Infrastructure.Sec;

/// <summary>
/// Infrastructure-internal abstraction over the SEC EDGAR filing-index fetch + EX-99.1 earnings-release
/// exhibit selection + HTML-strip so the analyzer (spec 74) is fully offline-testable and does not need to
/// know SEC URL patterns. Given a company CIK and the dashed accession number, the reader fetches the
/// filing's <c>{accession}-index.html</c>, parses the document table, selects the earnings-release exhibit
/// (exact <c>EX-99.1</c>, else an <c>EX-99.*</c> fallback, never the primary 8-K), fetches it, and returns
/// its body as plain text produced by the shared HTML stripper. A 403, transport error, timeout, missing
/// exhibit, or unparseable index reports its failure mode via the returned
/// <see cref="SecEarningsReleaseReadResult"/> rather than throwing; caller-requested cancellation still
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface ISecEarningsReleaseReader
{
    Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct);
}
