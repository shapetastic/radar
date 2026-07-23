namespace Radar.Infrastructure.Trademarks;

/// <summary>
/// Infrastructure-internal abstraction over the USPTO trademark search API GET + parse so the collector is
/// fully offline-testable (tests supply fixture filings; the real reader uses <c>HttpClient</c> +
/// <c>System.Text.Json</c>). An owner with no recent filings, an unreachable endpoint, or a blank API key
/// each reports its mode via the returned <see cref="TrademarkSearchReadResult"/> rather than swallowing it;
/// caller-requested cancellation still throws <see cref="OperationCanceledException"/>.
/// </summary>
internal interface ITrademarkSearchReader
{
    /// <summary>
    /// Reads trademark applications whose owner matches <paramref name="ownerName"/> and whose filing date is
    /// on or after <paramref name="filingFloor"/> (a bounded single page).
    /// </summary>
    Task<TrademarkSearchReadResult> ReadAsync(string ownerName, DateOnly filingFloor, CancellationToken ct);

    /// <summary>
    /// The human-viewable USPTO trademark query URL for the same owner + filing floor — used as the evidence
    /// <c>SourceUrl</c> provenance link (there is no stable per-owner landing page). One builder produces both
    /// the fetched URL and this link so they can never disagree.
    /// </summary>
    string QueryUrl(string ownerName, DateOnly filingFloor);
}
