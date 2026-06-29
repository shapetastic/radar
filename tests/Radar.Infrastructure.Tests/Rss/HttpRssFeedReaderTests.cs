using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Rss;

namespace Radar.Infrastructure.Tests.Rss;

public sealed class HttpRssFeedReaderTests
{
    private const string ValidRss = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0">
          <channel>
            <title>Acme IR</title>
            <link>https://acme.test</link>
            <description>Acme investor news</description>
            <item>
              <title>Acme launches widget</title>
              <link>https://acme.test/news/1</link>
              <description>The widget is now available.</description>
              <pubDate>Mon, 01 Jun 2026 13:00:00 GMT</pubDate>
            </item>
            <item>
              <title>Acme opens plant</title>
              <link>https://acme.test/news/2</link>
              <description>A new plant.</description>
              <pubDate>Tue, 02 Jun 2026 09:30:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private const string RssWithContentEncoded = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>Acme IR</title>
            <link>https://acme.test</link>
            <description>Acme investor news</description>
            <item>
              <title>Acme signs contract</title>
              <link>https://acme.test/news/1</link>
              <description>Short teaser.</description>
              <content:encoded>Full body: Acme signed a multi-year contract with a major customer.</content:encoded>
              <pubDate>Mon, 01 Jun 2026 13:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private const string AtomWithContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Acme IR</title>
          <id>https://acme.test/atom</id>
          <updated>2026-06-01T13:00:00Z</updated>
          <entry>
            <title>Acme signs contract</title>
            <id>https://acme.test/news/1</id>
            <link href="https://acme.test/news/1"/>
            <updated>2026-06-01T13:00:00Z</updated>
            <summary>Short teaser.</summary>
            <content type="text">Full body: Acme signed a multi-year contract with a major customer.</content>
          </entry>
        </feed>
        """;

    private const string RssWithWhitespaceContentEncoded = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>Acme IR</title>
            <link>https://acme.test</link>
            <description>Acme investor news</description>
            <item>
              <title>Acme signs contract</title>
              <link>https://acme.test/news/1</link>
              <description>Short teaser.</description>
              <content:encoded>   </content:encoded>
              <pubDate>Mon, 01 Jun 2026 13:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private const string AtomWithWhitespaceContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Acme IR</title>
          <id>https://acme.test/atom</id>
          <updated>2026-06-01T13:00:00Z</updated>
          <entry>
            <title>Acme signs contract</title>
            <id>https://acme.test/news/1</id>
            <link href="https://acme.test/news/1"/>
            <updated>2026-06-01T13:00:00Z</updated>
            <summary>Short teaser.</summary>
            <content type="text">   </content>
          </entry>
        </feed>
        """;

    private static HttpRssFeedReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<HttpRssFeedReader>.Instance);

    private const string EmptyRss = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0">
          <channel>
            <title>Acme IR</title>
            <link>https://acme.test</link>
            <description>Acme investor news</description>
          </channel>
        </rss>
        """;

    [Fact]
    public async Task ReadAsync_ValidRss_ParsesItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidRss));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Equal(RssFeedReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        var items = result.Items;
        Assert.Equal(2, items.Count);

        var first = items[0];
        Assert.Equal("Acme launches widget", first.Title);
        Assert.Equal("https://acme.test/news/1", first.Link);
        Assert.Equal("The widget is now available.", first.Summary);
        Assert.NotNull(first.PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 13, 0, 0, TimeSpan.Zero), first.PublishedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task ReadAsync_RssContentEncoded_CapturedAsContent()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, RssWithContentEncoded));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(
            "Full body: Acme signed a multi-year contract with a major customer.",
            item.Content);
        Assert.Equal("Short teaser.", item.Summary);
    }

    [Fact]
    public async Task ReadAsync_AtomContent_CapturedAsContent()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, AtomWithContent));

        var result = await reader.ReadAsync("https://acme.test/atom", CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(
            "Full body: Acme signed a multi-year contract with a major customer.",
            item.Content);
        Assert.Equal("Short teaser.", item.Summary);
    }

    [Fact]
    public async Task ReadAsync_WhitespaceContentEncoded_ContentIsNull()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, RssWithWhitespaceContentEncoded));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Null(item.Content);
        Assert.Equal("Short teaser.", item.Summary);
    }

    [Fact]
    public async Task ReadAsync_WhitespaceAtomContent_ContentIsNull()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, AtomWithWhitespaceContent));

        var result = await reader.ReadAsync("https://acme.test/atom", CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Null(item.Content);
        Assert.Equal("Short teaser.", item.Summary);
    }

    [Fact]
    public async Task ReadAsync_NoContentElement_ContentIsNullSummaryPreserved()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidRss));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        var first = result.Items[0];
        Assert.Null(first.Content);
        Assert.Equal("The widget is now available.", first.Summary);
    }

    [Fact]
    public async Task ReadAsync_QuietButValidFeed_ReturnsSuccessWithNoItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyRss));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Equal(RssFeedReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessStatus_ReturnsHttpErrorWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.NotFound, "missing"));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Equal(RssFeedReadOutcome.HttpError, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Contains("404", result.Detail);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_MalformedXml_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not <xml"));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Equal(RssFeedReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Equal(RssFeedReadOutcome.Unreachable, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Equal(RssFeedReadOutcome.Timeout, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidRss));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync("https://acme.test/rss", cts.Token));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/rss+xml"),
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
