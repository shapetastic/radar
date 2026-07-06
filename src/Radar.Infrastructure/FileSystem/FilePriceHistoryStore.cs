using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Prices;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk reference store for daily price history (AD-14): one JSON file per ticker at
/// <c>{RootDirectory}/{ticker}.json</c>. This is the price VALIDATION/REFERENCE dataset — consumed by nothing in
/// the scoring/evidence/signal/report path today, existing only for a future price-efficacy backtest. It reuses
/// the shared <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> + <see cref="RadarFileStoreJson.Options"/>
/// scaffolding (the "reuse over copy" rule) so its on-disk shape and graceful-degrade posture cannot diverge
/// from the other file stores. All file I/O and JSON stay confined to Infrastructure (AD-5).
/// <para>
/// <b>Merge / dedupe posture — APPEND-MERGE by <see cref="PriceBar.Date"/> (last-write-wins per date).</b> Price
/// history is append-mostly: successive runs fetch overlapping windows, and each day's final OHLC is fixed once
/// the day closes. On <see cref="WriteAsync"/> the store reads any existing file, UNIONS existing + new bars
/// KEYED BY <see cref="PriceBar.Date"/> — a new bar for an existing date REPLACES the stored one (last-write-wins
/// per date, so an intraday/partial bar is corrected once the day settles) — orders the union ascending by date,
/// and writes. The <i>file</i> is a rewritten mirror (like <c>FileScoreSnapshotStore</c>'s upsert-by-Id), but
/// each <i>bar</i> is stable-by-date. This mirrors the spirit of AD-1's immutable-vs-upsert split without
/// touching evidence (price is not evidence). Insert-only-append that rejected a changed bar would strand
/// partial last-day bars — rejected.
/// </para>
/// <para>
/// Best-effort (AD-8): a disk failure on write logs a warning and returns the attempted path (never throws); a
/// missing file on read returns <c>null</c>, and an unreadable/malformed file logs a warning and returns
/// <c>null</c> — a bad price file never crashes a run.
/// </para>
/// </summary>
public sealed class FilePriceHistoryStore : IPriceHistoryStore
{
    private readonly FilePriceHistoryStoreOptions _options;
    private readonly ILogger<FilePriceHistoryStore> _logger;

    public FilePriceHistoryStore(
        FilePriceHistoryStoreOptions options,
        ILogger<FilePriceHistoryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<string> WriteAsync(PriceHistory history, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(history);

        var sanitized = FileTickerKey.Sanitize(history.Ticker);
        if (sanitized is null)
        {
            // A blank/invalid ticker has no safe filename — skip rather than write outside the root. Return a
            // path-shaped placeholder under the root so the caller still gets a non-null value (best-effort).
            _logger.LogWarning(
                "Price history ticker '{Ticker}' is blank or contains invalid filename characters; skipping write.",
                history.Ticker);
            return Path.Combine(_options.RootDirectory, "(invalid-ticker).json");
        }

        var path = Path.Combine(_options.RootDirectory, sanitized + ".json");

        // Merge the incoming bars onto any existing file (last-write-wins per Date), ordered ascending by Date.
        var existing = await ReadFromPathAsync(path, ct).ConfigureAwait(false);
        var merged = MergeBars(existing?.Bars, history.Bars);
        var toWrite = history with { Bars = merged };

        var json = JsonSerializer.Serialize(toWrite, RadarFileStoreJson.Options);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Wrote {BarCount} price bar(s) for '{Ticker}' to {Path}.",
                merged.Count,
                history.Ticker,
                path);
        }

        return path;
    }

    public async Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct)
    {
        var sanitized = FileTickerKey.Sanitize(ticker);
        if (sanitized is null)
        {
            return null;
        }

        var path = Path.Combine(_options.RootDirectory, sanitized + ".json");
        return await ReadFromPathAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<PriceHistory?> ReadFromPathAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PriceHistory>(text, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // One unreadable/malformed price file must not break a run — degrade to null (AD-8).
            _logger.LogWarning(ex, "Failed to read price file '{Path}'; skipping.", path);
            return null;
        }
    }

    /// <summary>
    /// Unions existing + new bars keyed by <see cref="PriceBar.Date"/> (new wins per date), ascending by date.
    /// </summary>
    private static IReadOnlyList<PriceBar> MergeBars(
        IReadOnlyList<PriceBar>? existing, IReadOnlyList<PriceBar> incoming)
    {
        var byDate = new Dictionary<DateOnly, PriceBar>();
        if (existing is not null)
        {
            foreach (var bar in existing)
            {
                byDate[bar.Date] = bar;
            }
        }

        foreach (var bar in incoming)
        {
            // Last-write-wins per date: the freshly-fetched bar replaces any stored bar for that date.
            byDate[bar.Date] = bar;
        }

        return byDate.Values.OrderBy(b => b.Date).ToList();
    }
}
