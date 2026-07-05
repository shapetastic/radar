using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Radar.Application.Prices;

namespace Radar.Infrastructure.Prices;

/// <summary>
/// GETs a ticker's daily price history from the verified keyless Yahoo chart v8 endpoint
/// (<c>https://query1.finance.yahoo.com/v8/finance/chart/{TICKER}?interval=1d&amp;range=...</c>) and projects the
/// index-aligned <c>timestamp[]</c> + <c>indicators.quote[0].{open,high,low,close,volume}[]</c> +
/// <c>indicators.adjclose[0].adjclose[]</c> arrays into <see cref="PriceBar"/>s (UTC calendar <c>Date</c> from
/// <c>DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.Date</c>, <c>decimal</c> prices). A bar with a
/// <c>null</c> in ANY of its fields (timestamp/open/high/low/close/adjclose/volume — an unpriced/holiday/partial
/// row) is SKIPPED, never fabricated (leaving an honest gap in the reference dataset).
/// <para>
/// This is a SEPARATE seam from the evidence collectors (AD-14): it returns no <c>CollectedEvidence</c>, is not
/// in the collector <c>IEnumerable</c>, and confines all HTTP/JSON/Yahoo specifics to Infrastructure (AD-5). It
/// NEVER throws on a bad response — a non-success status maps to <see cref="PriceHistoryReadOutcome.HttpError"/>
/// (HTTP 429 to a distinct <see cref="PriceHistoryReadOutcome.RateLimited"/>), a transport error to
/// <see cref="PriceHistoryReadOutcome.Unreachable"/>, the request's own timeout to
/// <see cref="PriceHistoryReadOutcome.Timeout"/>, and a null/empty <c>chart.result</c> or absent/ragged arrays
/// to <see cref="PriceHistoryReadOutcome.Malformed"/>; a valid document with zero bars is
/// <see cref="PriceHistoryReadOutcome.Success"/> with zero bars. Only caller-requested cancellation propagates.
/// No API key, no secret, no paid service (stooq was rejected — a JS anti-bot wall; see spec 92).
/// </para>
/// </summary>
internal sealed class HttpPriceHistoryReader : IPriceHistoryReader
{
    // Parse case-insensitively so the lowercase/camelCase Yahoo keys ("timestamp", "adjclose", ...) bind.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

    public string SourceName => "yahoo-chart-v8";

    public async Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        // Honour caller cancellation before the request, independent of transport timing.
        ct.ThrowIfCancellationRequested();

        var url = _options.BuildRequestUrl(ticker);

        string body;
        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                _logger.LogWarning(
                    "Price history for '{Ticker}' returned HTTP 429 (rate limited); skipping.", ticker);
                return PriceHistoryReadResult.Failure(
                    PriceHistoryReadOutcome.RateLimited, "HTTP 429 (rate limited)");
            }

            if (!response.IsSuccessStatusCode)
            {
                // A delisted/unknown ticker returns HTTP 404 (verified) — that, and any other non-success
                // status, maps to the generic HttpError outcome.
                _logger.LogWarning(
                    "Price history for '{Ticker}' returned non-success status {StatusCode}; skipping.",
                    ticker,
                    (int)response.StatusCode);
                return PriceHistoryReadResult.Failure(
                    PriceHistoryReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}");
            }

            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Price history for '{Ticker}' fetch failed; skipping.", ticker);
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
            _logger.LogWarning(ex, "Price history for '{Ticker}' timed out; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Timeout, "request timed out");
        }

        return Parse(body, ticker);
    }

    /// <summary>
    /// Parses the Yahoo chart JSON. A null/empty <c>chart.result</c> (the verified delisted-ticker body), an
    /// absent <c>timestamp</c> array, an absent <c>quote[0]</c>, or any OHLCV/adjclose array whose length differs
    /// from <c>timestamp</c> (ragged) is <see cref="PriceHistoryReadOutcome.Malformed"/>. A present but empty
    /// <c>timestamp</c> array is <see cref="PriceHistoryReadOutcome.Success"/> with zero bars. A bar with a null
    /// in any field (timestamp/open/high/low/close/adjclose/volume) is skipped, never backfilled.
    /// </summary>
    private PriceHistoryReadResult Parse(string body, string ticker)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning("Price history for '{Ticker}' returned an empty body; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, "empty body");
        }

        ChartEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ChartEnvelope>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Price history for '{Ticker}' returned malformed JSON; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, "malformed JSON");
        }

        var result = envelope?.Chart?.Result;
        if (result is null || result.Length == 0 || result[0] is null)
        {
            // chart.result null/empty — a delisted/unknown ticker (verified 404 body) or an unexpected shape.
            _logger.LogWarning(
                "Price history for '{Ticker}' returned no chart result; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, "null/empty chart.result");
        }

        var chart = result[0]!;
        var timestamps = chart.Timestamp;
        if (timestamps is null)
        {
            _logger.LogWarning(
                "Price history for '{Ticker}' had no timestamp array; skipping.", ticker);
            return PriceHistoryReadResult.Failure(PriceHistoryReadOutcome.Malformed, "absent timestamp array");
        }

        var n = timestamps.Length;
        if (n == 0)
        {
            // A valid document with zero bars is a non-error outcome (a ticker with no bars in range).
            return PriceHistoryReadResult.Success(Array.Empty<PriceBar>());
        }

        var quote = chart.Indicators?.Quote is { Length: > 0 } quotes ? quotes[0] : null;
        var adjClose = chart.Indicators?.Adjclose is { Length: > 0 } adj ? adj[0]?.Adjclose : null;
        if (quote is null
            || IsRagged(quote.Open, n) || IsRagged(quote.High, n) || IsRagged(quote.Low, n)
            || IsRagged(quote.Close, n) || IsRagged(quote.Volume, n) || IsRagged(adjClose, n))
        {
            _logger.LogWarning(
                "Price history for '{Ticker}' had absent/ragged OHLCV arrays; skipping.", ticker);
            return PriceHistoryReadResult.Failure(
                PriceHistoryReadOutcome.Malformed, "absent/ragged OHLCV arrays");
        }

        var bars = new List<PriceBar>(n);
        for (var i = 0; i < n; i++)
        {
            var ts = timestamps[i];
            var open = quote.Open![i];
            var high = quote.High![i];
            var low = quote.Low![i];
            var close = quote.Close![i];
            var adjCloseVal = adjClose![i];
            var volume = quote.Volume![i];

            // A usable bar requires every field: a null in ANY of timestamp/open/high/low/close/adjclose/volume
            // is an unpriced/holiday/partial row. Never fabricate a value (e.g. AdjClose=close would silently
            // strip the split/dividend adjustment; OHL=close would invent a synthetic zero-range bar that never
            // traded) — a validation/backtest reference dataset must show an honest gap, so skip the whole bar
            // rather than backfilling (AD-14).
            if (ts is null || open is null || high is null || low is null
                || close is null || adjCloseVal is null || volume is null)
            {
                continue;
            }

            var date = DateTimeOffset.FromUnixTimeSeconds(ts.Value).UtcDateTime.Date;

            bars.Add(new PriceBar(
                Date: DateOnly.FromDateTime(date),
                Open: open.Value,
                High: high.Value,
                Low: low.Value,
                Close: close.Value,
                AdjClose: adjCloseVal.Value,
                Volume: volume.Value));
        }

        return PriceHistoryReadResult.Success(bars);
    }

    private static bool IsRagged<T>(T[]? array, int expectedLength) =>
        array is null || array.Length != expectedLength;

    private sealed record ChartEnvelope(
        [property: JsonPropertyName("chart")] ChartPayload? Chart);

    private sealed record ChartPayload(
        [property: JsonPropertyName("result")] ChartResult?[]? Result);

    private sealed record ChartResult(
        [property: JsonPropertyName("timestamp")] long?[]? Timestamp,
        [property: JsonPropertyName("indicators")] ChartIndicators? Indicators);

    private sealed record ChartIndicators(
        [property: JsonPropertyName("quote")] ChartQuote?[]? Quote,
        [property: JsonPropertyName("adjclose")] ChartAdjClose?[]? Adjclose);

    private sealed record ChartQuote(
        [property: JsonPropertyName("open")] decimal?[]? Open,
        [property: JsonPropertyName("high")] decimal?[]? High,
        [property: JsonPropertyName("low")] decimal?[]? Low,
        [property: JsonPropertyName("close")] decimal?[]? Close,
        [property: JsonPropertyName("volume")] long?[]? Volume);

    private sealed record ChartAdjClose(
        [property: JsonPropertyName("adjclose")] decimal?[]? Adjclose);
}
