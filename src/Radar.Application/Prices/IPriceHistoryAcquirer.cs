namespace Radar.Application.Prices;

/// <summary>
/// The opt-in price-history acquisition step (AD-14). Enumerates the seeded watch universe, reads each ticker's
/// daily bars, and persists them to the price reference store. Runs as a Worker step DISTINCT from the collector
/// loop and OUTSIDE <c>IRadarPipeline</c> — it produces no evidence, no signal, and no score; price cannot enter
/// the evidence → signal → score path through this seam.
/// </summary>
public interface IPriceHistoryAcquirer
{
    /// <summary>
    /// Acquires and stores daily price history for every non-blank ticker in the watch universe. A per-ticker
    /// read/store failure is logged and does not abort the others; only caller cancellation propagates.
    /// </summary>
    Task AcquireAsync(CancellationToken ct);
}
