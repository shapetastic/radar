using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Gdelt;

namespace Radar.Infrastructure.Tests.Gdelt;

public sealed class HttpGdeltNewsReaderTests
{
    // A well-formed DOC ArtList response: two Mercury Systems articles with the GDELT punctuation spacing.
    private const string ValidArticles = """
        {
          "articles": [
            {
              "url": "https://finance.yahoo.com/news/mrcy-mid-cap-defense",
              "url_mobile": "https://m.finance.yahoo.com/news/mrcy",
              "title": "Mercury Systems , Inc . ( MRCY ): Among The Best Mid Cap Defense Stocks",
              "seendate": "20260627T123000Z",
              "socialimage": "https://img.example/mrcy.png",
              "domain": "finance.yahoo.com",
              "language": "English",
              "sourcecountry": "United States"
            },
            {
              "url": "https://defensenews.example/mercury-contract",
              "title": "Mercury Systems wins new radar processing award",
              "seendate": "20260625T090000Z",
              "domain": "defensenews.example",
              "language": "English",
              "sourcecountry": "United States"
            }
          ]
        }
        """;

    private const string EmptyArticles = """
        { "articles": [] }
        """;

    private const string AbsentArticles = """
        { "status": "ok", "count": 0 }
        """;

    private static readonly GdeltNewsQuery Query = new(
        QueryPhrase: "Mercury Systems",
        Timespan: "2w",
        MaxRecords: 25,
        EnglishOnly: true)
    {
        // Zero delay keeps the 429-retry path instant and offline.
        MaxRetriesOn429 = 1,
        RetryDelay = TimeSpan.Zero,
    };

    private static HttpGdeltNewsReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<HttpGdeltNewsReader>.Instance);

    [Fact]
    public async Task ReadAsync_ValidArticles_ParsesItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidArticles));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Items.Count);

        var first = result.Items[0];
        Assert.Equal("https://finance.yahoo.com/news/mrcy-mid-cap-defense", first.Url);
        Assert.Equal(
            "Mercury Systems , Inc . ( MRCY ): Among The Best Mid Cap Defense Stocks", first.Title);
        Assert.Equal("finance.yahoo.com", first.Domain);
        Assert.Equal("English", first.Language);
        Assert.Equal("United States", first.SourceCountry);

        // seendate parses to the exact UTC instant.
        Assert.Equal(new DateTimeOffset(2026, 6, 27, 12, 30, 0, TimeSpan.Zero), first.SeenDate);
        Assert.Equal(TimeSpan.Zero, first.SeenDate!.Value.Offset);

        Assert.Equal(new DateTimeOffset(2026, 6, 25, 9, 0, 0, TimeSpan.Zero), result.Items[1].SeenDate);
    }

    [Fact]
    public async Task ReadAsync_EmptyArticles_ReturnsSuccessWithNoItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyArticles));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_AbsentArticles_ReturnsSuccessWithNoItems()
    {
        // A valid object with no articles array is a company with no coverage, not an error.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, AbsentArticles));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Success, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_ArticleMissingUrl_IsSkipped()
    {
        const string body = """
            {
              "articles": [
                { "title": "No url here", "seendate": "20260627T123000Z", "domain": "x.example" },
                { "url": "https://ok.example/a", "title": "Has url", "domain": "ok.example" }
              ]
            }
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("https://ok.example/a", item.Url);
        Assert.Null(item.SeenDate);
    }

    [Fact]
    public async Task ReadAsync_Http429_ReturnsRateLimitedAfterRetriesWithoutThrowing()
    {
        // GDELT throttles hard. The reader owns a single bounded retry (MaxRetriesOn429 = 1), so the handler
        // is called twice, then it still returns RateLimited without throwing.
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests, "rate limited");
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.RateLimited, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
        Assert.Equal(2, handler.CallCount); // initial attempt + 1 retry
    }

    [Fact]
    public async Task ReadAsync_Http429_NoRetriesConfigured_CallsOnceAndReturnsRateLimited()
    {
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests, "rate limited");
        var reader = CreateReader(handler);
        var query = Query with { MaxRetriesOn429 = 0 };

        var result = await reader.ReadAsync(query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.RateLimited, result.Outcome);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ReadAsync_OtherNonSuccess_ReturnsHttpErrorWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.HttpError, result.Outcome);
        Assert.Contains("500", result.Detail);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not { json"));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_EmptyBody_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, string.Empty));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_UnexpectedRootShape_ReturnsMalformedWithoutThrowing(string body)
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("""{ "articles": {} }""")]
    [InlineData("""{ "articles": "unexpected" }""")]
    [InlineData("""{ "articles": 42 }""")]
    public async Task ReadAsync_ArticlesPresentButNotArray_ReturnsMalformed(string body)
    {
        // `articles` present with a non-array shape is a bad/changed payload, not a quiet company; it must not
        // masquerade as a successful zero-coverage read.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_ArticlesExplicitNull_ReturnsSuccessWithNoItems()
    {
        // An explicit null `articles` is treated as no coverage (like an absent key), not a malformed payload.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, """{ "articles": null }"""));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Success, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Unreachable, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(GdeltReadOutcome.Timeout, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidArticles));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(Query, cts.Token));
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

    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
