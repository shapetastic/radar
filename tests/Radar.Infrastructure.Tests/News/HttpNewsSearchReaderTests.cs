using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

public sealed class HttpNewsSearchReaderTests
{
    // A realistic Google News RSS search response: two items, each with the "<headline> - <Publisher>" title,
    // a stable news.google.com landing <link>, a <guid>, an RFC 1123 <pubDate>, and the <source> publisher.
    private const string ValidFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/">
          <channel>
            <title>"Rocket Lab" - Google News</title>
            <link>https://news.google.com/search?q=Rocket+Lab</link>
            <item>
              <title>Rocket Lab wins new launch contract - SpaceNews</title>
              <link>https://news.google.com/rss/articles/AAA111</link>
              <guid isPermaLink="false">AAA111</guid>
              <pubDate>Thu, 02 Jul 2026 12:40:51 GMT</pubDate>
              <description>&lt;a href="x"&gt;Rocket Lab wins new launch contract&lt;/a&gt;</description>
              <source url="https://spacenews.com">SpaceNews</source>
            </item>
            <item>
              <title>Rocket Lab expands Neutron production - Reuters</title>
              <link>https://news.google.com/rss/articles/BBB222</link>
              <guid isPermaLink="false">BBB222</guid>
              <pubDate>Wed, 01 Jul 2026 08:15:00 GMT</pubDate>
              <description>&lt;a href="y"&gt;Rocket Lab expands Neutron production&lt;/a&gt;</description>
              <source url="https://reuters.com">Reuters</source>
            </item>
          </channel>
        </rss>
        """;

    private const string EmptyChannel = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>"Quiet Company" - Google News</title>
            <link>https://news.google.com/search?q=Quiet+Company</link>
          </channel>
        </rss>
        """;

    private static readonly NewsSearchQuery Query = new(
        QueryPhrase: "Rocket Lab",
        MaxRecords: 25,
        EnglishOnly: true);

    private static readonly DateTimeOffset FixedNow = new(2026, 7, 2, 13, 0, 0, TimeSpan.Zero);

    private static HttpNewsSearchReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<HttpNewsSearchReader>.Instance, new FixedTime(FixedNow));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task ReadAsync_ValidFeed_ParsesItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidFeed));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Items.Count);

        var first = result.Items[0];
        Assert.Equal("https://news.google.com/rss/articles/AAA111", first.Url);
        // Title is kept as-is, INCLUDING the " - Publisher" suffix (headline intact for provenance).
        Assert.Equal("Rocket Lab wins new launch contract - SpaceNews", first.Title);
        Assert.Equal("SpaceNews", first.SourceName);
        // pubDate parses to the exact UTC instant.
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 12, 40, 51, TimeSpan.Zero), first.PublishedAt);
        Assert.Equal(TimeSpan.Zero, first.PublishedAt!.Value.Offset);

        var second = result.Items[1];
        Assert.Equal("https://news.google.com/rss/articles/BBB222", second.Url);
        Assert.Equal("Rocket Lab expands Neutron production - Reuters", second.Title);
        Assert.Equal("Reuters", second.SourceName);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero), second.PublishedAt);
    }

    [Fact]
    public async Task ReadAsync_EmptyChannel_ReturnsSuccessWithNoItems()
    {
        // A valid <rss>/<channel> with no <item>s is a company with no coverage, not an error.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyChannel));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_ItemMissingLink_IsSkipped()
    {
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <item>
                  <title>No link here - SomePublisher</title>
                  <pubDate>Thu, 02 Jul 2026 12:40:51 GMT</pubDate>
                  <source url="https://some.com">SomePublisher</source>
                </item>
                <item>
                  <title>Has a link - OkPublisher</title>
                  <link>https://news.google.com/rss/articles/OK</link>
                  <source url="https://ok.com">OkPublisher</source>
                </item>
              </channel>
            </rss>
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("https://news.google.com/rss/articles/OK", item.Url);
        Assert.Equal("OkPublisher", item.SourceName);
        Assert.Null(item.PublishedAt);
    }

    [Fact]
    public async Task ReadAsync_ItemWithoutSourceElement_DerivesSourceNameFromTitleSuffix()
    {
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <item>
                  <title>Foo - Bar</title>
                  <link>https://news.google.com/rss/articles/CCC</link>
                </item>
              </channel>
            </rss>
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        var item = Assert.Single(result.Items);
        // No <source>, so the publisher is derived from the " - Bar" title suffix.
        Assert.Equal("Bar", item.SourceName);
        Assert.Equal("Foo - Bar", item.Title);
    }

    [Fact]
    public async Task ReadAsync_Http429_ReturnsRateLimitedWithoutThrowing()
    {
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests, "rate limited");
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.RateLimited, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
        Assert.Equal(1, handler.CallCount); // no retry
    }

    [Fact]
    public async Task ReadAsync_OtherNonSuccess_ReturnsHttpErrorWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.HttpError, result.Outcome);
        Assert.Contains("500", result.Detail);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_MalformedBody_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "not xml"));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_EmptyBody_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, string.Empty));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_UnexpectedRoot_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(
            new StubHandler(HttpStatusCode.OK, "<html><body>not an rss feed</body></html>"));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.Unreachable, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(NewsSearchReadOutcome.Timeout, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidFeed));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(Query, cts.Token));
    }

    [Fact]
    public async Task ReadAsync_EnglishOnly_TogglesLocaleParamsOnTheRequestUrl()
    {
        // EnglishOnly=true appends the en-US locale params; EnglishOnly=false omits them (the flag is honored,
        // not silently ignored). The query phrase is always URL-encoded into q=.
        var handler = new CapturingHandler(HttpStatusCode.OK, ValidFeed);
        var reader = CreateReader(handler);

        await reader.ReadAsync(Query with { EnglishOnly = true }, CancellationToken.None);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("q=Rocket%20Lab", handler.LastRequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("hl=en-US&gl=US&ceid=US:en", handler.LastRequestUri.Query, StringComparison.Ordinal);

        await reader.ReadAsync(Query with { EnglishOnly = false }, CancellationToken.None);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("q=Rocket%20Lab", handler.LastRequestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("hl=en-US", handler.LastRequestUri.Query, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 177 §3: the RSS payload Radar used to discard — exact bounded description, deterministic plain
    // text, explicit truncation, publisher-site URL, retrieval instant.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_PreservesExactDescription_PlainText_SourceUrl_AndRetrievalInstant()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidFeed));

        var result = await reader.ReadAsync(Query, CancellationToken.None);
        var first = result.Items[0];

        // DescriptionRaw is the EXACT element content (the escaped HTML decoded once by the XML layer).
        Assert.Equal("<a href=\"x\">Rocket Lab wins new launch contract</a>", first.DescriptionRaw);
        // DescriptionText is the deterministic plain-text rendering (tags stripped, whitespace collapsed).
        Assert.Equal("Rocket Lab wins new launch contract", first.DescriptionText);
        Assert.False(first.DescriptionTruncated);
        // <source url> is publisher-SITE provenance; <link> stays the Google landing URL.
        Assert.Equal("https://spacenews.com", first.PublisherSiteUrl);
        Assert.Equal("https://news.google.com/rss/articles/AAA111", first.Url);
        // Retrieval time comes from the injected TimeProvider, one instant per response.
        Assert.Equal(FixedNow, first.RetrievedAt);
        Assert.All(result.Items, i => Assert.Equal(FixedNow, i.RetrievedAt));
    }

    [Fact]
    public async Task ReadAsync_AbsentOrBlankDescription_IsNull_NeverACopiedHeadline()
    {
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <item>
                  <title>No description here - Pub</title>
                  <link>https://news.google.com/rss/articles/NODESC</link>
                </item>
                <item>
                  <title>Blank description here - Pub</title>
                  <link>https://news.google.com/rss/articles/BLANKDESC</link>
                  <description>   </description>
                </item>
              </channel>
            </rss>
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query with { QueryPhrase = "x" }, CancellationToken.None);

        Assert.All(result.Items, item =>
        {
            Assert.Null(item.DescriptionRaw);
            Assert.Null(item.DescriptionText);
            Assert.False(item.DescriptionTruncated);
        });
    }

    [Fact]
    public async Task ReadAsync_OversizedDescription_IsBoundedInUtf8Bytes_WithExplicitTruncationFlag()
    {
        // Multi-byte content ('é' = 2 UTF-8 bytes) proves the bound is BYTES, not chars, and that the cut
        // lands on a character boundary.
        var oversized = new string('é', HttpNewsSearchReader.MaxDescriptionUtf8Bytes); // 2× the byte bound
        var body = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <item>
                  <title>Big one - Pub</title>
                  <link>https://news.google.com/rss/articles/BIG</link>
                  <description>{oversized}</description>
                </item>
              </channel>
            </rss>
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query with { QueryPhrase = "x" }, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.True(item.DescriptionTruncated);
        Assert.Equal(
            HttpNewsSearchReader.MaxDescriptionUtf8Bytes,
            Encoding.UTF8.GetByteCount(item.DescriptionRaw!));
        // A prefix is never passed off as complete — and the retained prefix is exactly the leading chars.
        Assert.Equal(new string('é', HttpNewsSearchReader.MaxDescriptionUtf8Bytes / 2), item.DescriptionRaw);
    }

    [Fact]
    public async Task ReadAsync_OversizedDescription_CutLandingMidSurrogatePair_DropsTheWholePair()
    {
        // 'a' (1 byte) then astral chars ('𝛼' = one surrogate PAIR = 4 UTF-8 bytes): every pair boundary
        // sits at 1 + 4k bytes, so the byte bound can never fall exactly on one — the cut must drop the
        // whole straddling pair rather than split it.
        var oversized = "a" + string.Concat(
            Enumerable.Repeat("\U0001D6FC", HttpNewsSearchReader.MaxDescriptionUtf8Bytes / 2));
        var body = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <item>
                  <title>Astral one - Pub</title>
                  <link>https://news.google.com/rss/articles/ASTRAL</link>
                  <description>{oversized}</description>
                </item>
              </channel>
            </rss>
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query with { QueryPhrase = "x" }, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.True(item.DescriptionTruncated);
        // The whole pair straddling the bound is dropped: exactly maxBytes − 3 bytes survive, and the
        // retained prefix ends on a complete pair, never an unpaired high surrogate.
        Assert.Equal(
            HttpNewsSearchReader.MaxDescriptionUtf8Bytes - 3,
            Encoding.UTF8.GetByteCount(item.DescriptionRaw!));
        Assert.False(char.IsHighSurrogate(item.DescriptionRaw![^1]));
        Assert.Equal(
            "a" + string.Concat(
                Enumerable.Repeat("\U0001D6FC", (HttpNewsSearchReader.MaxDescriptionUtf8Bytes - 4) / 4)),
            item.DescriptionRaw);
    }

    [Fact]
    public async Task ReadAsync_NonAbsoluteOrNonHttpSourceUrl_DegradesToNull()
    {
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0">
              <channel>
                <item>
                  <title>Relative source url - Pub</title>
                  <link>https://news.google.com/rss/articles/REL</link>
                  <source url="/relative/path">Pub</source>
                </item>
                <item>
                  <title>Ftp source url - Pub</title>
                  <link>https://news.google.com/rss/articles/FTP</link>
                  <source url="ftp://example.com">Pub</source>
                </item>
              </channel>
            </rss>
            """;
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query with { QueryPhrase = "x" }, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Null(item.PublisherSiteUrl));
        // The publisher NAME still resolves from the <source> element text as before.
        Assert.All(result.Items, item => Assert.Equal("Pub", item.SourceName));
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
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
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
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
