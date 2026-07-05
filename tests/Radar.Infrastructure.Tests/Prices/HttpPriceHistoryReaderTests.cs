using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Prices;
using Radar.Infrastructure.Prices;

namespace Radar.Infrastructure.Tests.Prices;

public sealed class HttpPriceHistoryReaderTests
{
    private const string Ticker = "MRCY";

    // Two aligned daily bars. 1782480600 = 2026-06-06T00:10:00Z (UTC date 2026-06-06); 1782739800 = 2026-06-09.
    private const long Ts0 = 1782480600;
    private const long Ts1 = 1782739800;

    private static readonly DateOnly Date0 =
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(Ts0).UtcDateTime);

    private static readonly DateOnly Date1 =
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(Ts1).UtcDateTime);

    private const string TwoBarChart = """
        {"chart":{"result":[{
          "meta":{"currency":"USD","symbol":"MRCY","regularMarketPrice":126.21},
          "timestamp":[1782480600,1782739800],
          "indicators":{
            "quote":[{"open":[120.5,122.0],"high":[125.0,127.5],"low":[119.0,121.0],"close":[124.25,126.21],"volume":[1000000,1500000]}],
            "adjclose":[{"adjclose":[124.00,126.00]}]
          }
        }],"error":null}}
        """;

    // Second bar's close is null (an unpriced/holiday gap) — that bar must be SKIPPED, the first parsed.
    private const string NullCloseChart = """
        {"chart":{"result":[{
          "timestamp":[1782480600,1782739800],
          "indicators":{
            "quote":[{"open":[120.5,122.0],"high":[125.0,127.5],"low":[119.0,121.0],"close":[124.25,null],"volume":[1000000,1500000]}],
            "adjclose":[{"adjclose":[124.00,126.00]}]
          }
        }],"error":null}}
        """;

    // Verified delisted-ticker body: chart.result is null.
    private const string DelistedChart =
        """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""";

    // A valid document with zero bars (empty aligned arrays) → Success with zero bars.
    private const string ZeroBarChart = """
        {"chart":{"result":[{
          "timestamp":[],
          "indicators":{"quote":[{"open":[],"high":[],"low":[],"close":[],"volume":[]}],"adjclose":[{"adjclose":[]}]}
        }],"error":null}}
        """;

    // Ragged: close has 1 entry but timestamp has 2 → Malformed.
    private const string RaggedChart = """
        {"chart":{"result":[{
          "timestamp":[1782480600,1782739800],
          "indicators":{"quote":[{"open":[120.5,122.0],"high":[125.0,127.5],"low":[119.0,121.0],"close":[124.25],"volume":[1000000,1500000]}]}
        }],"error":null}}
        """;

    private static HttpPriceHistoryReader CreateReader(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new PriceReaderOptions { Range = "1y" },
            NullLogger<HttpPriceHistoryReader>.Instance);

    [Fact]
    public async Task ReadDailyAsync_AlignedBars_ParseIntoPriceBars()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, TwoBarChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PriceHistoryReadOutcome.Success, result.Outcome);
        Assert.Equal("yahoo-chart-v8", result.Source);
        Assert.Equal(2, result.Bars.Count);

        var bar0 = result.Bars[0];
        Assert.Equal(Date0, bar0.Date);
        Assert.Equal(120.5m, bar0.Open);
        Assert.Equal(125.0m, bar0.High);
        Assert.Equal(119.0m, bar0.Low);
        Assert.Equal(124.25m, bar0.Close);
        Assert.Equal(124.00m, bar0.AdjClose);
        Assert.Equal(1000000L, bar0.Volume);

        Assert.Equal(Date1, result.Bars[1].Date);
        Assert.Equal(126.21m, result.Bars[1].Close);
    }

    [Fact]
    public async Task ReadDailyAsync_NullCloseBar_IsSkipped_RemainingBarsParse()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, NullCloseChart)));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var bar = Assert.Single(result.Bars);
        Assert.Equal(Date0, bar.Date);
        Assert.Equal(124.25m, bar.Close);
    }

    [Fact]
    public async Task ReadDailyAsync_NullResult_ReturnsMalformed()
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

        Assert.True(result.IsSuccess);
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
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.TooManyRequests, "rate limited")));

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
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, TwoBarChart)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadDailyAsync(Ticker, cts.Token));
    }

    [Fact]
    public async Task ReadDailyAsync_MalformedJson_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, "{ not valid json")));

        var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);

        Assert.Equal(PriceHistoryReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadDailyAsync_NoDetail_ContainsAdviceLanguage()
    {
        // No advice language ("buy"/"sell"/"target"/"guaranteed"/"safe bet") may appear in any Detail (AD-9).
        string[] banned = ["buy", "sell", "target", "guaranteed", "safe bet"];

        foreach (var chart in new[] { DelistedChart, RaggedChart })
        {
            var reader = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.OK, chart)));
            var result = await reader.ReadDailyAsync(Ticker, CancellationToken.None);
            var detail = result.Detail ?? string.Empty;
            foreach (var word in banned)
            {
                Assert.DoesNotContain(word, detail, StringComparison.OrdinalIgnoreCase);
            }
        }

        var http = CreateReader(new RoutingHandler(_ => Json(HttpStatusCode.NotFound, "x")));
        var httpResult = await http.ReadDailyAsync(Ticker, CancellationToken.None);
        Assert.NotNull(httpResult.Detail);
        foreach (var word in banned)
        {
            Assert.DoesNotContain(word, httpResult.Detail!, StringComparison.OrdinalIgnoreCase);
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
