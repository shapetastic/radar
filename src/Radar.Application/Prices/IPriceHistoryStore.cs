namespace Radar.Application.Prices;

/// <summary>
/// The persistence seam for the price reference dataset (AD-14): <c>data/prices/{ticker}.json</c>, consumed by
/// NOTHING in the scoring/evidence/signal/report path today — it exists solely for a future price-efficacy
/// validation/backtest spec. Best-effort (AD-8): a disk/read failure logs and degrades (write returns the
/// attempted path, read returns null) rather than throwing.
/// </summary>
public interface IPriceHistoryStore
{
    /// <summary>
    /// Persists a ticker's price history to <c>{RootDirectory}/{ticker}.json</c>. Merges the new bars into any
    /// existing file, deduping by <see cref="PriceBar.Date"/> (last-write-wins per date), ordered ascending by
    /// date. Best-effort (AD-8): a disk failure logs and returns the attempted path, never throws. Returns the
    /// path.
    /// </summary>
    Task<string> WriteAsync(PriceHistory history, CancellationToken ct);

    /// <summary>
    /// Reads a ticker's persisted history, or <c>null</c> if none exists / it is unreadable (the future
    /// backtest's read seam). Best-effort: a malformed/unreadable file logs and returns null, never throws.
    /// </summary>
    Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct);
}
