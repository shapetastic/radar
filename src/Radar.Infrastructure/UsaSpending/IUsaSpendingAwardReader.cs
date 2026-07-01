namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// Infrastructure-internal abstraction over the USASpending.gov <c>spending_by_award</c> POST + parse so
/// the collector is fully offline-testable (tests supply fixture awards; the real reader uses
/// <c>HttpClient</c> + <c>System.Text.Json</c>). A recipient with no matching awards, an unreachable
/// endpoint, or a silently-ignored-filter firehose response each reports its mode via the returned
/// <see cref="UsaSpendingReadResult"/> rather than swallowing it; caller-requested cancellation still
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface IUsaSpendingAwardReader
{
    Task<UsaSpendingReadResult> ReadAsync(UsaSpendingAwardQuery query, CancellationToken ct);
}
