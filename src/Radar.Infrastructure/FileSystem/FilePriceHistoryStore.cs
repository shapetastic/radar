using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Prices;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk reference store for daily price history (AD-14, files-first AD-8). Writes one JSON file per ticker to
/// <c>{RootDirectory}/{ticker}.json</c> via the shared <see cref="GracefulFileWriter"/> +
/// <see cref="RadarFileStoreJson.Options"/> scaffolding (reused so the on-disk shape and graceful-degrade
/// posture cannot diverge from the other file stores). This store is consumed by NOTHING in the
/// scoring/evidence/signal/report path — it exists solely as the reference/validation dataset a future
/// price-efficacy backtest will read.
/// <para>
/// <b>Merge posture — APPEND-MERGE by <see cref="PriceBar.Date"/>, last-write-wins per date, ascending.</b>
/// Price history is append-mostly: successive runs fetch overlapping windows, and each trading day's final OHLC
/// is fixed once the day closes. On <see cref="WriteAsync"/> the store reads any existing file, UNIONS existing
/// + new bars keyed by <c>Date</c> — a new bar for an existing <c>Date</c> REPLACES the stored one (correcting an
/// intraday/partial bar once the day settles) — orders the union ascending by <c>Date</c>, and writes. The
/// <em>file</em> is a rewritten mirror (like <c>FileScoreSnapshotStore</c>'s upsert-by-Id), but each <em>bar</em>
/// is stable-by-date. Insert-only-append that rejected a changed bar would strand partial last-day bars —
/// deliberately rejected.
/// </para>
/// <para>
/// Best-effort (AD-8): a disk write failure logs + returns the attempted path (never throws); a
/// missing/unreadable/malformed file on read logs + returns <see langword="null"/>. A blank/invalid ticker
/// (one containing <see cref="Path.GetInvalidFileNameChars"/>) is logged and skipped — the store never writes
/// outside its root.
/// </para>
/// </summary>
public sealed class FilePriceHistoryStore : IPriceHistoryStore
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

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

        var path = ResolvePath(history.Ticker);
        if (path is null)
        {
            _logger.LogWarning(
                "Price history ticker '{Ticker}' is blank or contains invalid path characters; skipping write.",
                history.Ticker);
            return string.Empty;
        }

        // Append-merge: union existing + new bars keyed by Date (new replaces same-date), ascending.
        var existing = await ReadAsync(history.Ticker, ct).ConfigureAwait(false);
        var mergedBars = MergeBars(existing?.Bars, history.Bars);
        var toWrite = history with { Bars = mergedBars };

        var json = JsonSerializer.Serialize(toWrite, RadarFileStoreJson.Options);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Wrote {BarCount} price bar(s) for {Ticker} to {Path}.",
                mergedBars.Count,
                history.Ticker,
                path);
        }

        return path;
    }

    public async Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct)
    {
        var path = ResolvePath(ticker);
        if (path is null)
        {
            _logger.LogWarning(
                "Price history ticker '{Ticker}' is blank or contains invalid path characters; returning null.",
                ticker);
            return null;
        }

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
            // One unreadable/malformed price file must not crash the caller; degrade to no reference data.
            _logger.LogWarning(ex, "Failed to read price history file '{Path}'; skipping.", path);
            return null;
        }
    }

    /// <summary>
    /// Unions existing + incoming bars keyed by <see cref="PriceBar.Date"/> — an incoming bar for an existing
    /// date REPLACES the stored one (last-write-wins per date) — and returns them ordered ascending by date,
    /// exactly one bar per date.
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
            byDate[bar.Date] = bar; // last-write-wins per date (incoming corrects a settled day)
        }

        return byDate.Values.OrderBy(b => b.Date).ToList();
    }

    /// <summary>
    /// Builds the <c>{RootDirectory}/{ticker}.json</c> path for a lowercased ticker, or <see langword="null"/>
    /// when the ticker is blank or contains a <see cref="Path.GetInvalidFileNameChars"/> character (so the store
    /// never writes outside its root).
    /// </summary>
    private string? ResolvePath(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return null;
        }

        var name = ticker.Trim().ToLowerInvariant();
        if (name.AsSpan().IndexOfAny(InvalidFileNameChars) >= 0)
        {
            return null;
        }

        return Path.Combine(_options.RootDirectory, name + ".json");
    }
}
