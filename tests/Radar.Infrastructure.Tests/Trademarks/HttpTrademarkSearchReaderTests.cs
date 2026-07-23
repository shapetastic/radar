using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Trademarks;

namespace Radar.Infrastructure.Tests.Trademarks;

public sealed class HttpTrademarkSearchReaderTests
{
    private const string ApiKeyEnvVar = "RADAR_TEST_USPTO_KEY";

    // A well-formed USPTO trademark response: two applications for the owner plus the envelope count.
    private const string ValidResults = """
        {
          "count": 27,
          "results": [
            { "serialNumber": "97000001", "markText": "WD-40 SPECIALIST", "filingDate": "2026-05-12" },
            { "serialNumber": "97000002", "markText": "BLUE WORKS", "filingDate": "2026-03-01" }
          ]
        }
        """;

    private const string EmptyResults = """
        { "count": 0, "results": [] }
        """;

    // Rows carrying an unparseable/absent filingDate must be skipped, not coerced to a min-value date. Only the
    // one row with a valid filing date counts.
    private const string UnparseableFilingDates = """
        {
          "count": 3,
          "results": [
            { "serialNumber": "97000001", "markText": "Valid row", "filingDate": "2026-05-12" },
            { "serialNumber": "97000002", "markText": "Bad date", "filingDate": "not-a-date" },
            { "serialNumber": "97000003", "markText": "Absent date" }
          ]
        }
        """;

    private const string NoResultsArray = """
        { "count": 0 }
        """;

    private static readonly DateOnly FilingFloor = new(2026, 1, 1);

    private static HttpTrademarkSearchReader CreateReader(
        HttpMessageHandler handler, TrademarkCollectorOptions? options = null) =>
        new(
            new HttpClient(handler),
            NullLogger<HttpTrademarkSearchReader>.Instance,
            options ?? new TrademarkCollectorOptions { ApiKeyEnvVar = ApiKeyEnvVar });

    // Save/restore the env var around each test so state never leaks across tests. NEVER a real key.
    private static IDisposable WithApiKey(string? value) => new EnvVarScope(ApiKeyEnvVar, value);

    [Fact]
    public async Task ReadAsync_ValidResults_ParsesFilingsCountSerialsMarksAndDates()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result!.FilingCount);
        // count is kept as the API-reported cross-check.
        Assert.Equal(27, result.Result.ApiReportedTotal);

        var first = result.Result.Filings[0];
        Assert.Equal("97000001", first.SerialNumber);
        Assert.Equal("WD-40 SPECIALIST", first.MarkText);
        Assert.Equal(new DateOnly(2026, 5, 12), first.FilingDate);
    }

    [Fact]
    public async Task ReadAsync_EmptyResultsArray_ReturnsSuccessWithZeroFilings()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyResults));

        var result = await reader.ReadAsync("Nobody Brands, Inc.", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.FilingCount);
        Assert.Empty(result.Result.Filings);
    }

    [Fact]
    public async Task ReadAsync_RowsWithUnparseableFilingDate_AreSkipped()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, UnparseableFilingDates));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Success, result.Outcome);
        // Only the single row with a valid filingDate survives; the bad/absent dates are dropped, not coerced.
        var filing = Assert.Single(result.Result!.Filings);
        Assert.Equal(1, result.Result.FilingCount);
        Assert.Equal("97000001", filing.SerialNumber);
        Assert.Equal(new DateOnly(2026, 5, 12), filing.FilingDate);
    }

    [Fact]
    public async Task ReadAsync_MissingResultsArray_ReturnsMalformed()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, NoResultsArray));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Malformed, result.Outcome);
        Assert.Null(result.Result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_UnexpectedRootShape_ReturnsMalformed(string body)
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformed()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not { json"));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_BlankApiKey_ReturnsMissingApiKeyWithNoHttpCall()
    {
        // A blank/absent configured key must degrade with NO HTTP call — assert the handler is never invoked.
        using var _ = WithApiKey(null);
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.MissingApiKey, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessStatus_ReturnsHttpError()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.Forbidden, "forbidden"));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.HttpError, result.Outcome);
        Assert.Contains("403", result.Detail);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeout()
    {
        using var _ = WithApiKey("test-key");
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachable()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.Equal(TrademarkSearchOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync("WD-40 Company", FilingFloor, cts.Token));
    }

    [Fact]
    public async Task ReadAsync_SetsXApiKeyHeaderFromEnvVar()
    {
        using var _ = WithApiKey("secret-value-123");
        var handler = new HeaderCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        await reader.ReadAsync("WD-40 Company", FilingFloor, CancellationToken.None);

        Assert.True(handler.CapturedHeaders!.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("secret-value-123", Assert.Single(values!));
    }

    [Fact]
    public void QueryUrl_EncodesOwnerAndFilingFloor()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        var url = reader.QueryUrl("WD-40 Company", FilingFloor);

        Assert.StartsWith(
            "https://api.uspto.gov/api/v1/trademark/applications/search?q=", url, StringComparison.Ordinal);
        Assert.Contains("&rows=", url, StringComparison.Ordinal);
        // The owner name is URL-encoded, so a raw space never appears in the URL.
        Assert.DoesNotContain(' ', url);
        var decoded = Uri.UnescapeDataString(url);
        Assert.Contains("owner:WD-40 Company", decoded, StringComparison.Ordinal);
        Assert.Contains("2026-01-01", decoded, StringComparison.Ordinal);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CountingStubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class HeaderCapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public System.Net.Http.Headers.HttpRequestHeaders? CapturedHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedHeaders = request.Headers;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    // Sets an environment variable for the scope of a test and restores its prior value on dispose, so a test
    // never leaks env state into another test. Never carries a real key.
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
