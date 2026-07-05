using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Prices;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk reference/validation store for daily price history (AD-14). Writes one JSON file per ticker to
/// <c>{RootDirectory}/{ticker}.json</c> using the shared <see cref="RadarFileStoreJson.Options"/> shape and
/// the shared <see cref="GracefulFileWriter"/> writer, so the on-disk shape and graceful-degrade posture
/// cannot diverge from the other file stores. All file I/O and JSON stay confined to Infrastructure (AD-5);
/// the Application sees only <see cref="IPriceHistoryStore"/>. This store is consumed by NOTHING in the
/// scoring/evidence/signal/report path today — it exists solely for a future price-efficacy validation spec.
/// <remarks>
/// <b>Merge / dedupe posture: APPEND-MERGE by <see cref="PriceBar.Date"/> (last-write-wins per date).</b>
/// Price history is append-mostly: successive runs fetch overlapping windows, and each day's final OHLC is
/// fixed once the day closes. On <see cref="WriteAsync"/> the store reads the existing file (if any), UNIONs
/// the existing + new bars keyed by <c>Date</c> where a new bar for an existing <c>Date</c> REPLACES the
/// stored one (last-write-wins per date — corrects an intraday/partial bar once the day settles), orders the
/// union ascending by <c>Date</c>, and writes. Like the score-snapshot store the <i>file</i> is a rewritten
/// mirror, but each <c>bar</c> is stable-by-date. (Insert-only-append rejecting a changed bar would strand
/// partial last-day bars — rejected.) A disk failure degrades gracefully (warn + return the attempted path
/// on write; warn + return null on read) and never crashes the run.
/// </remarks>
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

        var ticker = SanitizeTicker(history.Ticker);
        if (ticker is null)
        {
            // Never write outside the root: a blank/invalid ticker is logged and skipped.
            _logger.LogWarning(
                "Price history ticker '{Ticker}' is blank or contains invalid file-name characters; skipping write.",
                history.Ticker);
            return string.Empty;
        }

        var path = Path.Combine(_options.RootDirectory, ticker + ".json");

        // APPEND-MERGE by Date (last-write-wins): union the existing bars with the new bars, new replaces same-Date.
        var existing = await ReadFromPathAsync(path, ct).ConfigureAwait(false);
        var merged = MergeBars(existing?.Bars, history.Bars);
        var toWrite = history with { Bars = merged };

        var json = JsonSerializer.Serialize(toWrite, RadarFileStoreJson.Options);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Wrote {Bars} price bar(s) for '{Ticker}' to {Path}.", merged.Count, ticker, path);
        }

        return path;
    }

    public async Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct)
    {
        var sanitized = SanitizeTicker(ticker);
        if (sanitized is null)
        {
            _logger.LogWarning(
                "Price history ticker '{Ticker}' is blank or contains invalid file-name characters; returning null.",
                ticker);
            return null;
        }

        var path = Path.Combine(_options.RootDirectory, sanitized + ".json");
        return await ReadFromPathAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unions existing + new bars keyed by <see cref="PriceBar.Date"/> (new replaces same-Date stored bar),
    /// ordered ascending by Date. Guarantees exactly one bar per date.
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

        // New bars overwrite same-date stored bars (last-write-wins per date).
        foreach (var bar in incoming)
        {
            byDate[bar.Date] = bar;
        }

        return byDate.Values.OrderBy(b => b.Date).ToList();
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
            // One unreadable/malformed price file must never crash the run — log and degrade to null.
            _logger.LogWarning(ex, "Failed to read price history file '{Path}'; returning null.", path);
            return null;
        }
    }

    /// <summary>
    /// Lower-cases and validates the ticker for safe use as a file name: returns null when the ticker is
    /// blank or contains any <see cref="Path.GetInvalidFileNameChars"/> (so a write can never escape the
    /// root or collide with a path separator).
    /// </summary>
    private static string? SanitizeTicker(string? ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return null;
        }

        var trimmed = ticker.Trim();
        if (trimmed.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }
}
