using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Prices;
using Radar.Infrastructure.Prices;

namespace Radar.Infrastructure.Tests.Prices;

public sealed class HttpPriceHistoryReaderTests
{
    private const string Ticker = "MRCY";

    // Two aligned bars. Timestamps are market-open instants on two consecutive UTC trading days.
    //   1782480600 = 2026-06-06T05:30:00Z -> UTC date 2026-06-06
    //   1782739800 = 2026-06-09T05:30:00Z -> UTC date 2026-06-09
    private const long Ts0 = 1782480600L;
    private const long Ts1 = 1782739800L;
    private static readonly DateOnly Date0 = DateOnly.FromDateTime(
        DateTimeOffset.FromUnixTimeSeconds(Ts0).UtcDateTime.Date);
    private static readonly DateOnly Date1 = DateOnly.FromDateTime(
        DateTimeOffset.FromUnixTimeSeconds(Ts1).UtcDateTime.Date);

    // Ts0 = 1782480600, Ts1 = 1782739800 inlined below (raw strings can't interpolate the {{ }}-heavy JSON).
    private const string TwoBarChart =
        """{"chart":{"result":[{"meta":{"currency":"USD","symbol":"MRCY","regularMarketPrice":126.21},"timestamp":[1782480600,1782739800],"indicators":{"quote":[{"open":[100.10,101.20],"high":[102.30,103.40],"low":[99.50,100.60],"close":[101.75,102.85],"volume":[123456,234567]}],"adjclose":[{"adjclose":[101.00,102.10]}]}}],"error":null}}""";

    // A null close in the second slot (and everything aligned) — the second bar must be skipped.
    private const string NullCloseChart =
        """{"chart":{"result":[{"timestamp":[1782480600,1782739800],"indicators":{"quote":[{"open":[100.10,101.20],"high":[102.30,103.40],"low":[99.50,100.60],"close":[101.75,null],"volume":[123456,234567]}],"adjclose":[{"adjclose":[101.00,102.10]}]}}],"error":null}}""";

    // A null OPEN in the second slot (close present) — the second bar must still be skipped, never backfilled.
    private const string NullOpenChart =
        """{"chart":{"result":[{"timestamp":[1782480600,1782739800],"indicators":{"quote":[{"open":[100.10,null],"high":[102.30,103.40],"low":[99.50,100.60],"close":[101.75,102.85],"volume":[123456,234567]}],"adjclose":[{"adjclose":[101.00,102.10]}]}}],"error":null}}""";

    // A null ADJCLOSE in the second slot (close present) — the second bar must still be skipped, never backfilled
    // (backfilling AdjClose from close would silently strip the split/dividend adjustment).
    private const string NullAdjCloseChart =
        """{"chart":{"result":[{"timestamp":[1782480600,1782739800],"indicators":{"quote":[{"open":[100.10,101.20],"high":[102.30,103.40],"low":[99.50,100.60],"close":[101.75,102.85],"volume":[123456,234567]}],"adjclose":[{"adjclose":[101.00,null]}]}}],"error":null}}""";

    // The verified delisted-ticker body: chart.result is null.
    private const string DelistedChart =
        """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""";

    // Ragged: volume array shorter than timestamp.
    private const string RaggedChart =
        """{"chart":{"result":[{"timestamp":[1782480600,1782739800],"indicators":{"quote":[{"open":[100.10,101.20],"high":[102.30,103.40],"low":[99.50,100.60],"close":[101.75,102.85],"volume":[123456]}],"adjclose":[{"adjclose":[101.00,102.10]}]}}],"error":null}}""";

    // A valid document with zero bars (empty timestamp array).
    private const string ZeroBarChart =
        """{"chart":{"result":[{"timestamp":[],"indicators":{"quote":[{"open":[],"high":[],"low":[],"close":[],"volume":[]}],"adjclose":[{"adjclose":[]}]}}],"error":null}}""";

    private static HttpPriceHistoryReader CreateReader(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new PriceReaderOptions { Range = "1y" },
            NullLogger<HttpPriceHistoryReader>.Instance);

    [Fact]
    public async Task ReadDailyAsync_AlignedBars_ParseIntoPriceBarsWithUtcDatesAndDecimals()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, TwoBarChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Bars.Count);

        var first = result.Bars[0];
        Assert.Equal(Date0, first.Date);
        Assert.Equal(100.10m, first.Open);
        Assert.Equal(102.30m, first.High);
        Assert.Equal(99.50m, first.Low);
        Assert.Equal(101.75m, first.Close);
        Assert.Equal(101.00m, first.AdjClose);
        Assert.Equal(123456L, first.Volume);

        var second = result.Bars[1];
        Assert.Equal(Date1, second.Date);
        Assert.Equal(102.85m, second.Close);
        Assert.Equal(234567L, second.Volume);
    }

    [Fact]
    public async Task ReadDailyAsync_NullCloseBar_IsSkipped_RemainingBarsParse()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, NullCloseChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        // The null-close row is dropped, not fabricated — only the first bar survives.
        var only = Assert.Single(result.Bars);
        Assert.Equal(Date0, only.Date);
        Assert.Equal(101.75m, only.Close);
    }

    [Fact]
    public async Task ReadDailyAsync_NullOpenBar_IsSkipped_NotBackfilledFromClose()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, NullOpenChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        // The second bar has a null open with a present close; it must be SKIPPED (an honest gap), never
        // backfilled with the close (which would invent a synthetic zero-range bar that never traded).
        var only = Assert.Single(result.Bars);
        Assert.Equal(Date0, only.Date);
        Assert.Equal(100.10m, only.Open);
    }

    [Fact]
    public async Task ReadDailyAsync_NullAdjCloseBar_IsSkipped_NotBackfilledFromClose()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, NullAdjCloseChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        // A null adjclose with a present close must be SKIPPED, never backfilled from close — doing so would
        // silently strip the split/dividend adjustment the backtest most relies on.
        var only = Assert.Single(result.Bars);
        Assert.Equal(Date0, only.Date);
        Assert.Equal(101.00m, only.AdjClose);
    }

    [Fact]
    public async Task ReadDailyAsync_NullChartResult_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, DelistedChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task ReadDailyAsync_RaggedArrays_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, RaggedChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_ZeroBars_ReturnsSuccessWithZeroBars()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, ZeroBarChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task ReadDailyAsync_Http404_ReturnsHttpError()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.NotFound, DelistedChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.HttpError, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ReadDailyAsync_Http429_ReturnsRateLimited()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json((HttpStatusCode)429, "too many requests")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.RateLimited, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_TransportError_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, TwoBarChart)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadDailyAsync(Ticker, cts.Token));
    }

    [Fact]
    public async Task ReadDailyAsync_NoDetailCarriesAdviceLanguage()
    {
        // Advice-free Detail (AD-9): a failure's Detail must not carry buy/sell/target/guaranteed/safe language.
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.NotFound, DelistedChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        var detail = result.Detail ?? string.Empty;
        foreach (var banned in new[] { "buy", "sell", "target", "guaranteed", "safe bet" })
        {
            Assert.DoesNotContain(banned, detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route = route;

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(_route(request));
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
