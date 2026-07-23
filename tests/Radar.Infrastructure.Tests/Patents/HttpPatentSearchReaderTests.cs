using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Patents;

namespace Radar.Infrastructure.Tests.Patents;

public sealed class HttpPatentSearchReaderTests
{
    private const string ApiKeyEnvVar = "RADAR_TEST_PATENTSVIEW_KEY";

    // A well-formed PatentsView response: two granted patents for the assignee plus the envelope fields.
    private const string ValidResults = """
        {
          "error": false,
          "count": 2,
          "total_hits": 37,
          "patents": [
            { "patent_id": "11111111", "patent_title": "Secure processing module", "patent_date": "2026-05-12" },
            { "patent_id": "22222222", "patent_title": "Radiation-hardened memory device", "patent_date": "2026-03-01" }
          ]
        }
        """;

    private const string EmptyResults = """
        { "error": false, "count": 0, "total_hits": 0, "patents": [] }
        """;

    private const string NoPatentsArray = """
        { "error": false, "count": 0, "total_hits": 0 }
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
        // total_hits is kept as the API-reported cross-check.
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
    public void QueryUrl_EncodesAssigneeAndGrantFloor()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        var url = reader.QueryUrl("Mercury Systems, Inc.", GrantFloor);

        Assert.StartsWith("https://search.patentsview.org/api/v1/patent/?q=", url, StringComparison.Ordinal);
        Assert.Contains("&f=", url, StringComparison.Ordinal);
        Assert.Contains("&o=", url, StringComparison.Ordinal);
        // The assignee name is URL-encoded, so a raw space never appears in the URL.
        Assert.DoesNotContain(' ', url);
        Assert.Contains("2026-01-01", Uri.UnescapeDataString(url), StringComparison.Ordinal);
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
