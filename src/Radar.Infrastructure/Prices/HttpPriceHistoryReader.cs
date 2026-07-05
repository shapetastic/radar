using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Prices;

namespace Radar.Infrastructure.Prices;

/// <summary>
/// Fetches a ticker's daily price bars from the verified KEYLESS Yahoo Finance <c>chart</c> endpoint
/// (<c>GET https://query1.finance.yahoo.com/v8/finance/chart/{TICKER}?interval=1d&amp;range={range}</c>) and
/// projects the index-aligned <c>timestamp[]</c> + <c>indicators.quote[0].{open,high,low,close,volume}[]</c>
/// + <c>indicators.adjclose[0].adjclose[]</c> arrays into <see cref="PriceBar"/>s: each <c>timestamp[i]</c>
/// is converted to a UTC <see cref="DateOnly"/> and every price to <c>decimal</c>. A bar whose
/// <c>timestamp</c> OR <c>close</c> is null (an unpriced/holiday gap) is SKIPPED, never fabricated.
/// <para>
/// The reader NEVER throws on a bad response (mirrors the SEC/USASpending readers): a non-success status →
/// <see cref="PriceHistoryReadOutcome.HttpError"/>, HTTP 429 → <see cref="PriceHistoryReadOutcome.RateLimited"/>,
/// a transport error → <see cref="PriceHistoryReadOutcome.Unreachable"/>, the request's own timeout →
/// <see cref="PriceHistoryReadOutcome.Timeout"/>, and null/empty <c>chart.result</c> / absent / ragged arrays
/// / unparseable JSON → <see cref="PriceHistoryReadOutcome.Malformed"/>; a valid document with zero bars is a
/// <see cref="PriceHistoryReadOutcome.Success"/> with zero bars. Only caller-requested cancellation
/// propagates. All HTTP/JSON/Yahoo specifics stay confined to Infrastructure (AD-5). No key/secret/paid
/// service; the endpoint needs only a browser-like <c>User-Agent</c> (wired on the named client). Price is
/// reference/validation data only — never evidence, never a signal, never a scoring input (AD-14).
/// </para>
/// </summary>
internal sealed class HttpPriceHistoryReader : IPriceHistoryReader
{
    private const string SourceLabel = "yahoo-chart-v8";

    private const string DefaultEndpointTemplate =
        "https://query1.finance.yahoo.com/v8/finance/chart/{0}?interval=1d&range={1}";

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

        // Honour caller cancellation before the request, independent of transport timing.
        ct.ThrowIfCancellationRequested();

        var url = BuildUrl(ticker);

        byte[] bytes;
        try
        {
            using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "Price history for '{Ticker}' returned HTTP 429 (rate limited); skipping.", ticker);
                return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.RateLimited, "HTTP 429");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Price history for '{Ticker}' returned non-success status {StatusCode}; skipping.",
                    ticker,
                    (int)response.StatusCode);
                return PriceHistoryReadResult.Failure(
                    PriceHistoryReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}");
            }

            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Price history for '{Ticker}' failed; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Unreachable, "transport error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; never hide it as a failure result.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
            _logger.LogWarning(ex, "Price history for '{Ticker}' timed out; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Timeout, "request timed out");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return ParseChart(document.RootElement, ticker);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Price history for '{Ticker}' returned malformed JSON; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, "malformed JSON");
        }
    }

    private string BuildUrl(string ticker)
    {
        var template = string.IsNullOrWhiteSpace(_options.EndpointTemplate)
            ? DefaultEndpointTemplate
            : _options.EndpointTemplate;

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            template,
            Uri.EscapeDataString(ticker),
            Uri.EscapeDataString(_options.Range));
    }

    /// <summary>
    /// Projects the Yahoo <c>chart</c> document into a typed result. The verified success shape is
    /// <c>chart.result[0]</c> carrying index-aligned <c>timestamp[]</c> and
    /// <c>indicators.quote[0].{open,high,low,close,volume}[]</c> (plus an optional
    /// <c>indicators.adjclose[0].adjclose[]</c>). Null/empty <c>result</c>, absent OHLCV arrays, or ragged
    /// arrays (length ≠ <c>timestamp</c> length) → <see cref="PriceHistoryReadOutcome.Malformed"/>; zero bars
    /// → <see cref="PriceHistoryReadOutcome.Success"/> with zero bars.
    /// </summary>
    private PriceHistoryReadResult ParseChart(JsonElement root, string ticker)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("chart", out var chart)
            || chart.ValueKind != JsonValueKind.Object
            || !chart.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
        {
            // The verified delisted-ticker body has "result": null; any absent/empty result is malformed.
            return Malformed(ticker, "null/empty chart.result");
        }

        var node = result[0];
        if (node.ValueKind != JsonValueKind.Object)
        {
            return Malformed(ticker, "chart.result[0] is not an object");
        }

        if (!node.TryGetProperty("timestamp", out var timestamps)
            || timestamps.ValueKind != JsonValueKind.Array)
        {
            return Malformed(ticker, "absent timestamp array");
        }

        if (!node.TryGetProperty("indicators", out var indicators)
            || indicators.ValueKind != JsonValueKind.Object
            || !indicators.TryGetProperty("quote", out var quote)
            || quote.ValueKind != JsonValueKind.Array
            || quote.GetArrayLength() == 0
            || quote[0].ValueKind != JsonValueKind.Object)
        {
            return Malformed(ticker, "absent indicators.quote[0]");
        }

        var q = quote[0];
        if (!TryGetArray(q, "open", out var open)
            || !TryGetArray(q, "high", out var high)
            || !TryGetArray(q, "low", out var low)
            || !TryGetArray(q, "close", out var close)
            || !TryGetArray(q, "volume", out var volume))
        {
            return Malformed(ticker, "absent OHLCV arrays");
        }

        var length = timestamps.GetArrayLength();
        if (open.GetArrayLength() != length
            || high.GetArrayLength() != length
            || low.GetArrayLength() != length
            || close.GetArrayLength() != length
            || volume.GetArrayLength() != length)
        {
            return Malformed(ticker, "ragged OHLCV arrays");
        }

        // adjclose is optional (some intervals omit it). When present and same-length we use it; otherwise
        // each bar falls back to its close (a factual, non-fabricated value), never Malformed on adjclose.
        JsonElement adjClose = default;
        var hasAdjClose = indicators.TryGetProperty("adjclose", out var adjCloseArr)
            && adjCloseArr.ValueKind == JsonValueKind.Array
            && adjCloseArr.GetArrayLength() > 0
            && adjCloseArr[0].ValueKind == JsonValueKind.Object
            && TryGetArray(adjCloseArr[0], "adjclose", out adjClose)
            && adjClose.GetArrayLength() == length;

        var timestampItems = timestamps.EnumerateArray().ToArray();
        var openItems = open.EnumerateArray().ToArray();
        var highItems = high.EnumerateArray().ToArray();
        var lowItems = low.EnumerateArray().ToArray();
        var closeItems = close.EnumerateArray().ToArray();
        var volumeItems = volume.EnumerateArray().ToArray();
        var adjCloseItems = hasAdjClose ? adjClose.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

        var bars = new List<PriceBar>(length);
        for (var i = 0; i < length; i++)
        {
            // Skip any bar whose timestamp OR close is null (unpriced/holiday gap) — never fabricate a value.
            if (!TryGetInt64(timestampItems[i], out var ts))
            {
                continue;
            }

            var closeValue = TryGetDecimal(closeItems[i]);
            if (closeValue is null)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime);

            // For the other fields, a null element falls back to the (present) close / zero volume rather than
            // dropping the whole bar — the bar is priced (close present), so it is a usable reference row.
            var openValue = TryGetDecimal(openItems[i]) ?? closeValue.Value;
            var highValue = TryGetDecimal(highItems[i]) ?? closeValue.Value;
            var lowValue = TryGetDecimal(lowItems[i]) ?? closeValue.Value;
            var volumeValue = TryGetInt64(volumeItems[i], out var v) ? v : 0L;
            var adjCloseValue = hasAdjClose
                ? TryGetDecimal(adjCloseItems[i]) ?? closeValue.Value
                : closeValue.Value;

            bars.Add(new PriceBar(
                Date: date,
                Open: openValue,
                High: highValue,
                Low: lowValue,
                Close: closeValue.Value,
                AdjClose: adjCloseValue,
                Volume: volumeValue));
        }

        _logger.LogInformation(
            "Price history for '{Ticker}': parsed {Bars} of {Rows} row(s).", ticker, bars.Count, length);

        return PriceHistoryReadResult.Success(bars, SourceLabel);
    }

    private PriceHistoryReadResult Malformed(string ticker, string detail)
    {
        _logger.LogWarning("Price history for '{Ticker}' was malformed ({Detail}); skipping.", ticker, detail);
        return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, detail);
    }

    private static bool TryGetArray(JsonElement parent, string name, out JsonElement array)
    {
        if (parent.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }

    private static decimal? TryGetDecimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value) ? value : null;

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
        {
            return true;
        }

        value = 0L;
        return false;
    }
}
