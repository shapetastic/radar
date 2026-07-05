namespace Radar.Application.Prices;

/// <summary>
/// Persists and reads a ticker's daily price history — the dedicated reference/validation store (AD-14),
/// consumed by NOTHING in the scoring/evidence/signal/report path today. It exists solely for a future
/// price-efficacy validation/backtest spec.
/// </summary>
public interface IPriceHistoryStore
{
    /// <summary>
    /// Persists a ticker's price history to <c>{RootDirectory}/{ticker}.json</c>. Merges the new bars into
    /// any existing file, deduping by <see cref="PriceBar.Date"/> (new bar replaces a same-Date stored bar,
    /// last-write-wins per date), ordered ascending by Date. Best-effort (AD-8): a disk failure logs +
    /// returns the attempted path, never throws. Returns the path (empty when the ticker is blank/invalid).
    /// </summary>
    Task<string> WriteAsync(PriceHistory history, CancellationToken ct);

    /// <summary>
    /// Reads a ticker's persisted history, or null if none exists / it is unreadable (the future backtest's
    /// read seam). Best-effort: a malformed/unreadable file logs + returns null, never throws.
    /// </summary>
    Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct);
}
