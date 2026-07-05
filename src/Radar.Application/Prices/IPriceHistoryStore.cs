namespace Radar.Application.Prices;

/// <summary>
/// Persists and reads a ticker's daily price history — the dedicated <c>data/prices/</c> reference store
/// (AD-14), consumed by NOTHING in the scoring/evidence/signal/report path today. This is the read seam the
/// future price-efficacy validation/backtest spec will consume. Both operations are best-effort (AD-8): a
/// disk/read failure logs and degrades (write returns the attempted path, read returns null) — never throws.
/// </summary>
public interface IPriceHistoryStore
{
    /// <summary>
    /// Persists a ticker's price history to <c>{RootDirectory}/{ticker}.json</c>. Merges the new bars into any
    /// existing file, deduping by <see cref="PriceBar.Date"/> (append-merge, last-write-wins per date,
    /// ascending). Best-effort (AD-8): a disk failure logs + returns the attempted path, never throws.
    /// Returns the path (empty when the ticker is blank/invalid and no file could be addressed).
    /// </summary>
    Task<string> WriteAsync(PriceHistory history, CancellationToken ct);

    /// <summary>
    /// Reads a ticker's persisted history, or <see langword="null"/> if none exists / it is unreadable (the
    /// future backtest's read seam). Best-effort: a malformed/unreadable file logs + returns null, never throws.
    /// </summary>
    Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct);
}
