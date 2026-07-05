using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Prices;
using Radar.Infrastructure.Prices;

namespace Radar.Infrastructure.Tests.Prices;

public sealed class HttpPriceHistoryReaderTests
{
    private const string Ticker = "MRCY";

    // Two aligned daily bars. Epoch seconds: 1782480600 = 2026-06-05 ~13:30Z (market open),
    // 1782739800 = 2026-06-08 ~13:30Z — both resolve to those UTC calendar dates.
    private const long Ts1 = 1782480600L;
    private const long Ts2 = 1782739800L;

    private static readonly DateOnly Date1 = DateOnly.FromDateTime(
        DateTimeOffset.FromUnixTimeSeconds(Ts1).UtcDateTime);

    private static readonly DateOnly Date2 = DateOnly.FromDateTime(
        DateTimeOffset.FromUnixTimeSeconds(Ts2).UtcDateTime);

    // Epoch seconds inlined (Ts1/Ts2) — plain (non-interpolated) JSON so the literal }} braces are unambiguous.
    private const string TwoBarSuccess = """
        {"chart":{"result":[{
           "meta":{"currency":"USD","symbol":"MRCY","regularMarketPrice":126.21},
           "timestamp":[1782480600,1782739800],
           "indicators":{
              "quote":[{"open":[120.5,122.0],"high":[125.0,127.5],"low":[119.0,121.5],"close":[124.25,126.21],"volume":[1000000,1250000]}],
              "adjclose":[{"adjclose":[124.00,126.00]}]
           }}],
           "error":null}}
        """;

    // The second bar's timestamp is present but its close is null (an unpriced/holiday gap): it must be skipped.
    private const string NullCloseBar = """
        {"chart":{"result":[{
           "timestamp":[1782480600,1782739800],
           "indicators":{
              "quote":[{"open":[120.5,null],"high":[125.0,null],"low":[119.0,null],"close":[124.25,null],"volume":[1000000,null]}],
              "adjclose":[{"adjclose":[124.00,null]}]
           }}],
           "error":null}}
        """;

    // The verified delisted-ticker body: chart.result is null.
    private const string NullResult =
        """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""";

    // A valid document whose timestamp/quote arrays are empty → Success with zero bars.
    private const string ZeroBars = """
        {"chart":{"result":[{"timestamp":[],"indicators":{"quote":[{"open":[],"high":[],"low":[],"close":[],"volume":[]}],"adjclose":[{"adjclose":[]}]}}],"error":null}}
        """;

    // Ragged: close array is one element short of timestamp → Malformed.
    private const string Ragged = """
        {"chart":{"result":[{
           "timestamp":[1782480600,1782739800],
           "indicators":{
              "quote":[{"open":[120.5,122.0],"high":[125.0,127.5],"low":[119.0,121.5],"close":[124.25],"volume":[1000000,1250000]}],
              "adjclose":[{"adjclose":[124.00,126.00]}]
           }}],
           "error":null}}
        """;

    private static HttpPriceHistoryReader CreateReader(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new PriceReaderOptions(),
            NullLogger<HttpPriceHistoryReader>.Instance);

    [Fact]
    public async Task ReadDailyAsync_AlignedArrays_ParsesBarsWithUtcDatesAndDecimals()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, TwoBarSuccess)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Bars.Count);

        var first = result.Bars[0];
        Assert.Equal(Date1, first.Date);
        Assert.Equal(120.5m, first.Open);
        Assert.Equal(125.0m, first.High);
        Assert.Equal(119.0m, first.Low);
        Assert.Equal(124.25m, first.Close);
        Assert.Equal(124.00m, first.AdjClose);
        Assert.Equal(1000000L, first.Volume);

        var second = result.Bars[1];
        Assert.Equal(Date2, second.Date);
        Assert.Equal(126.21m, second.Close);
        Assert.Equal(126.00m, second.AdjClose);
        Assert.Equal(1250000L, second.Volume);
    }

    [Fact]
    public async Task ReadDailyAsync_NullCloseBar_IsSkipped_NotFabricated()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, NullCloseBar)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        var bar = Assert.Single(result.Bars);
        Assert.Equal(Date1, bar.Date);
        Assert.Equal(124.25m, bar.Close);
    }

    [Fact]
    public async Task ReadDailyAsync_NullResult_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, NullResult)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task ReadDailyAsync_RaggedArrays_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, Ragged)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_ZeroBars_ReturnsSuccessWithZeroBars()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, ZeroBars)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task ReadDailyAsync_Http404_ReturnsHttpError()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.NotFound, NullResult)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.HttpError, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("buy", result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadDailyAsync_Http429_ReturnsRateLimited()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json((HttpStatusCode)429, "rate limited")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.RateLimited, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_TransportError_ReturnsUnreachable()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_RequestTimeout_ReturnsTimeout()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, TwoBarSuccess)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadDailyAsync(Ticker, cts.Token));
    }

    [Fact]
    public async Task ReadDailyAsync_NoDetail_ContainsNoAdviceLanguage()
    {
        // Sweep the failure Details for banned advice language (AD-9).
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.InternalServerError, "err")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        foreach (var banned in new[] { "buy", "sell", "target", "guaranteed", "safe bet" })
        {
            Assert.DoesNotContain(banned, result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
