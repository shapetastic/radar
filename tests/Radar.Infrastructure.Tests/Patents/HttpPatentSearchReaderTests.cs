using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Patents;

namespace Radar.Infrastructure.Tests.Patents;

public sealed class HttpPatentSearchReaderTests
{
    private const string ApiKeyEnvVar = "RADAR_TEST_PATENTSVIEW_KEY";

    // A well-formed USPTO ODP PFW Search response (docs-derived fixture): two granted patents for the assignee,
    // each nesting its bibliographic fields under applicationMetaData, plus the envelope total. The reader reads
    // the envelope total defensively (count first, then totalNumFound) — this fixture carries both.
    private const string ValidResults = """
        {
          "count": 37,
          "totalNumFound": 37,
          "patentFileWrapperDataBag": [
            { "applicationNumberText": "16123456",
              "applicationMetaData": {
                "patentNumber": "11111111",
                "inventionTitle": "Secure processing module",
                "grantDate": "2026-05-12",
                "firstApplicantName": "Mercury Systems, Inc." } },
            { "applicationNumberText": "16123457",
              "applicationMetaData": {
                "patentNumber": "22222222",
                "inventionTitle": "Radiation-hardened memory device",
                "grantDate": "2026-03-01",
                "firstApplicantName": "Mercury Systems, Inc." } }
          ]
        }
        """;

    private const string EmptyResults = """
        { "count": 0, "totalNumFound": 0, "patentFileWrapperDataBag": [] }
        """;

    // Rows carrying an unparseable/absent grantDate must be skipped, not coerced to a min-value date. Only the
    // one row with a valid grant date counts.
    private const string UnparseableGrantDates = """
        {
          "count": 3,
          "patentFileWrapperDataBag": [
            { "applicationMetaData": { "patentNumber": "11111111", "inventionTitle": "Valid row", "grantDate": "2026-05-12" } },
            { "applicationMetaData": { "patentNumber": "22222222", "inventionTitle": "Bad date", "grantDate": "not-a-date" } },
            { "applicationMetaData": { "patentNumber": "33333333", "inventionTitle": "Absent date" } }
          ]
        }
        """;

    private const string NoPatentsArray = """
        { "count": 0, "totalNumFound": 0 }
        """;

    private static readonly DateOnly GrantFloor = new(2026, 1, 1);

    private static HttpPatentSearchReader CreateReader(
        HttpMessageHandler handler, PatentCollectorOptions? options = null) =>
        new(
            new HttpClient(handler),
            NullLogger<HttpPatentSearchReader>.Instance,
            options ?? new PatentCollectorOptions { ApiKeyEnvVar = ApiKeyEnvVar });

    // Save/restore the env var around each test so state never leaks across tests. NEVER a real key.
    private static IDisposable WithApiKey(string? value) => new EnvVarScope(ApiKeyEnvVar, value);

    [Fact]
    public async Task ReadAsync_ValidResults_ParsesGrantsCountAndTitles()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result!.GrantCount);
        // The envelope total (count) is kept as the API-reported cross-check.
        Assert.Equal(37, result.Result.ApiReportedTotal);

        var first = result.Result.Grants[0];
        Assert.Equal("11111111", first.PatentId);
        Assert.Equal("Secure processing module", first.Title);
        Assert.Equal(new DateOnly(2026, 5, 12), first.GrantDate);
    }

    [Fact]
    public async Task ReadAsync_EmptyPatentsArray_ReturnsSuccessWithZeroGrants()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyResults));

        var result = await reader.ReadAsync("Nobody, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.GrantCount);
        Assert.Empty(result.Result.Grants);
    }

    [Fact]
    public async Task ReadAsync_RowsWithUnparseableGrantDate_AreSkipped()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, UnparseableGrantDates));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Success, result.Outcome);
        // Only the single row with a valid grantDate survives; the bad/absent dates are dropped, not coerced.
        var grant = Assert.Single(result.Result!.Grants);
        Assert.Equal(1, result.Result.GrantCount);
        Assert.Equal("11111111", grant.PatentId);
        Assert.Equal(new DateOnly(2026, 5, 12), grant.GrantDate);
    }

    [Fact]
    public async Task ReadAsync_MissingPatentsArray_ReturnsMalformed()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, NoPatentsArray));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Malformed, result.Outcome);
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

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformed()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not { json"));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_BlankApiKey_ReturnsMissingApiKeyWithNoHttpCall()
    {
        // A blank/absent configured key must degrade with NO HTTP call — assert the handler is never invoked.
        using var _ = WithApiKey(null);
        var handler = new CountingStubHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.MissingApiKey, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessStatus_ReturnsHttpError()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.Forbidden, "forbidden"));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.HttpError, result.Outcome);
        Assert.Contains("403", result.Detail);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeout()
    {
        using var _ = WithApiKey("test-key");
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachable()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(PatentSearchOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        using var _ = WithApiKey("test-key");
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, cts.Token));
    }

    [Fact]
    public async Task ReadAsync_SetsXApiKeyHeaderFromEnvVar()
    {
        using var _ = WithApiKey("secret-value-123");
        var handler = new HeaderCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.True(handler.CapturedHeaders!.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("secret-value-123", Assert.Single(values!));
    }

    [Fact]
    public void QueryUrl_ReturnsOdpSearchEndpoint()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        // The request is a POST with the query in the body, so the provenance link is the constant search
        // endpoint (default host + fixed path), not a per-assignee GET URL.
        var url = reader.QueryUrl("Mercury Systems, Inc.", GrantFloor);

        Assert.Equal("https://api.uspto.gov/api/v1/patent/applications/search", url);
    }

    [Fact]
    public async Task ReadAsync_PostsToDefaultBaseUrlAndPath()
    {
        using var _ = WithApiKey("test-key");
        var handler = new RequestCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(handler);

        await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.CapturedMethod);
        Assert.Equal(
            "https://api.uspto.gov/api/v1/patent/applications/search",
            handler.CapturedUri!.ToString());
    }

    [Fact]
    public async Task ReadAsync_HonoursBaseUrlOverride()
    {
        using var _ = WithApiKey("test-key");
        var handler = new RequestCapturingHandler(HttpStatusCode.OK, ValidResults);
        var reader = CreateReader(
            handler,
            new PatentCollectorOptions { ApiKeyEnvVar = ApiKeyEnvVar, BaseUrl = "https://odp.example.test" });

        await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(
            "https://odp.example.test/api/v1/patent/applications/search",
            handler.CapturedUri!.ToString());
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

    private sealed class RequestCapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? CapturedMethod { get; private set; }

        public Uri? CapturedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedMethod = request.Method;
            CapturedUri = request.RequestUri;
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
