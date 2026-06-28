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

    private static HttpRssFeedReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<HttpRssFeedReader>.Instance);

    [Fact]
    public async Task ReadAsync_ValidRss_ParsesItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidRss));

        var items = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Equal(2, items.Count);

        var first = items[0];
        Assert.Equal("Acme launches widget", first.Title);
        Assert.Equal("https://acme.test/news/1", first.Link);
        Assert.Equal("The widget is now available.", first.Summary);
        Assert.NotNull(first.PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 13, 0, 0, TimeSpan.Zero), first.PublishedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task ReadAsync_NonSuccessStatus_ReturnsEmptyWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        var items = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ReadAsync_MalformedXml_ReturnsEmptyWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not <xml"));

        var items = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsEmptyWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var items = await reader.ReadAsync("https://acme.test/rss", CancellationToken.None);

        Assert.Empty(items);
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
