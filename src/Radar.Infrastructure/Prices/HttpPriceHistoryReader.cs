using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Radar.Application.Prices;

namespace Radar.Infrastructure.Prices;

/// <summary>
/// Fetches a ticker's daily price history from the keyless Yahoo Finance <c>chart</c> endpoint
/// (<c>GET https://query1.finance.yahoo.com/v8/finance/chart/{TICKER}?interval=1d&amp;range={range}</c>,
/// verified reachable with a browser <c>User-Agent</c>, no cookie/crumb) and projects the index-aligned
/// <c>timestamp[]</c> + <c>indicators.quote[0].{open,high,low,close,volume}[]</c> +
/// <c>indicators.adjclose[0].adjclose[]</c> arrays into <see cref="PriceBar"/>s (each timestamp → a UTC calendar
/// date, each price → <c>decimal</c>). A bar whose <c>timestamp</c> or <c>close</c> is <c>null</c> (an
/// unpriced/holiday gap) is SKIPPED — never fabricated.
/// <para>
/// Typed graceful outcomes, mirroring the SEC/News readers — this reader NEVER throws on a bad response:
/// a non-success status → <see cref="PriceHistoryReadOutcome.HttpError"/> (HTTP 429 →
/// <see cref="PriceHistoryReadOutcome.RateLimited"/>), a transport error →
/// <see cref="PriceHistoryReadOutcome.Unreachable"/>, the request's own timeout →
/// <see cref="PriceHistoryReadOutcome.Timeout"/>, and a 200 whose <c>chart.result</c> is null/empty, whose
/// arrays are absent, or whose OHLCV arrays are ragged → <see cref="PriceHistoryReadOutcome.Malformed"/>; a
/// valid document with zero bars is <see cref="PriceHistoryReadOutcome.Success"/> with zero bars. The only
/// throw is genuine caller cancellation. All HTTP/JSON/Yahoo specifics stay in Infrastructure (AD-5); this is
/// NOT an <c>IEvidenceCollector</c> and produces NO evidence (AD-14).
/// </para>
/// </summary>
internal sealed class HttpPriceHistoryReader : IPriceHistoryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly PriceReaderOptions _options;
    private readonly ILogger<HttpPriceHistoryReader> _logger;

    public HttpPriceHistoryReader(
        HttpClient httpClient,
        PriceReaderOptions options,
        ILogger<HttpPriceHistoryReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        // Honour caller cancellation before the request, independent of transport timing (mirrors the SEC reader).
        ct.ThrowIfCancellationRequested();

        var url = string.Format(
            CultureInfo.InvariantCulture,
            _options.EndpointTemplate,
            Uri.EscapeDataString(ticker.Trim()),
            _options.Range);

        string body;
        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                _logger.LogWarning(
                    "Price history for {Ticker} returned HTTP 429 (rate limited); skipping.", ticker);
                return PriceHistoryReadResult.Failure(
                    PriceHistoryReadOutcome.RateLimited, "HTTP 429 (rate limited)");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Price history for {Ticker} returned non-success status {StatusCode}; skipping.",
                    ticker,
                    (int)response.StatusCode);
                return PriceHistoryReadResult.Failure(
                    PriceHistoryReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}");
            }

            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Price history for {Ticker} fetch failed; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Unreachable, "transport error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; do not hide it as a failure.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
            _logger.LogWarning(ex, "Price history for {Ticker} fetch timed out; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Timeout, "request timed out");
        }

        return Parse(body, ticker);
    }

    /// <summary>
    /// Parses the Yahoo <c>chart</c> JSON. A null/empty <c>chart.result</c> (the verified delisted-ticker body),
    /// absent arrays, or ragged OHLCV arrays are <see cref="PriceHistoryReadOutcome.Malformed"/>; a valid
    /// document whose <c>timestamp</c> array is empty is <see cref="PriceHistoryReadOutcome.Success"/> with zero
    /// bars. Never throws — a <see cref="JsonException"/> maps to Malformed.
    /// </summary>
    private PriceHistoryReadResult Parse(string body, string ticker)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning("Price history for {Ticker} returned an empty body; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, "empty body");
        }

        ChartResponse? document;
        try
        {
            document = JsonSerializer.Deserialize<ChartResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Price history for {Ticker} returned malformed JSON; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, "malformed JSON");
        }

        var result = document?.Chart?.Result;
        if (result is null || result.Count == 0 || result[0] is null)
        {
            _logger.LogWarning(
                "Price history for {Ticker} returned a null/empty chart.result; skipping.", ticker);
            return PriceHistoryReadResult.Failure(
                PriceHistoryReadOutcome.Malformed, "null/empty chart.result");
        }

        var series = result[0];
        var timestamps = series.Timestamp;
        var quote = series.Indicators?.Quote is { Count: > 0 } quotes ? quotes[0] : null;

        if (timestamps is null || quote is null)
        {
            _logger.LogWarning(
                "Price history for {Ticker} returned a document with absent timestamp/quote arrays; skipping.",
                ticker);
            return PriceHistoryReadResult.Failure(
                PriceHistoryReadOutcome.Malformed, "absent timestamp/quote arrays");
        }

        var open = quote.Open;
        var high = quote.High;
        var low = quote.Low;
        var close = quote.Close;
        var volume = quote.Volume;

        if (open is null || high is null || low is null || close is null || volume is null)
        {
            _logger.LogWarning(
                "Price history for {Ticker} returned a quote with absent OHLCV arrays; skipping.", ticker);
            return PriceHistoryReadResult.Failure(
                PriceHistoryReadOutcome.Malformed, "absent OHLCV arrays");
        }

        var n = timestamps.Count;
        if (open.Count != n || high.Count != n || low.Count != n || close.Count != n || volume.Count != n)
        {
            _logger.LogWarning(
                "Price history for {Ticker} returned ragged OHLCV arrays (length mismatch); skipping.", ticker);
            return PriceHistoryReadResult.Failure(
                PriceHistoryReadOutcome.Malformed, "ragged OHLCV arrays");
        }

        // The adjusted-close series is a bonus; use it only when present and aligned, otherwise fall back to the
        // (unadjusted) close per bar. Never fabricate — a missing adjclose is filled with the known close.
        var adjClose = series.Indicators?.AdjClose is { Count: > 0 } adjs ? adjs[0].AdjClose : null;
        var adjAligned = adjClose is not null && adjClose.Count == n;

        var bars = new List<PriceBar>(n);
        for (var i = 0; i < n; i++)
        {
            var ts = timestamps[i];
            var c = close[i];

            // Skip an unpriced/holiday gap: a bar with no timestamp or no close is not a usable bar.
            if (ts is null || c is null)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ts.Value).UtcDateTime);

            bars.Add(new PriceBar(
                Date: date,
                Open: open[i] ?? c.Value,
                High: high[i] ?? c.Value,
                Low: low[i] ?? c.Value,
                Close: c.Value,
                AdjClose: (adjAligned ? adjClose![i] : null) ?? c.Value,
                Volume: volume[i] ?? 0L));
        }

        return PriceHistoryReadResult.Success(bars);
    }

    private sealed record ChartResponse(
        [property: JsonPropertyName("chart")] ChartEnvelope? Chart);

    private sealed record ChartEnvelope(
        [property: JsonPropertyName("result")] IReadOnlyList<ChartResult>? Result);

    private sealed record ChartResult(
        [property: JsonPropertyName("timestamp")] IReadOnlyList<long?>? Timestamp,
        [property: JsonPropertyName("indicators")] ChartIndicators? Indicators);

    private sealed record ChartIndicators(
        [property: JsonPropertyName("quote")] IReadOnlyList<ChartQuote>? Quote,
        [property: JsonPropertyName("adjclose")] IReadOnlyList<ChartAdjClose>? AdjClose);

    private sealed record ChartQuote(
        [property: JsonPropertyName("open")] IReadOnlyList<decimal?>? Open,
        [property: JsonPropertyName("high")] IReadOnlyList<decimal?>? High,
        [property: JsonPropertyName("low")] IReadOnlyList<decimal?>? Low,
        [property: JsonPropertyName("close")] IReadOnlyList<decimal?>? Close,
        [property: JsonPropertyName("volume")] IReadOnlyList<long?>? Volume);

    private sealed record ChartAdjClose(
        [property: JsonPropertyName("adjclose")] IReadOnlyList<decimal?>? AdjClose);
}
