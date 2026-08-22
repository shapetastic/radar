using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

public sealed class HttpNewsArticleContentReaderTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly IPAddress PublicIp = IPAddress.Parse("93.184.216.34");

    private static NewsArticleContentReaderOptions Options(params string[] allowedDomains) =>
        new()
        {
            AllowedDomains = allowedDomains.Length == 0 ? ["allowed.example.com"] : allowedDomains,
            UserAgent = "RadarResearch contact@example.com",
            PerHostInterval = TimeSpan.Zero, // no real delays in tests; the pacing path itself still runs
        };

    private static HttpNewsArticleContentReader CreateReader(
        HttpMessageHandler handler,
        NewsArticleContentReaderOptions? options = null,
        IPAddress[]? resolvesTo = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options ?? Options(),
            new FixedTime(FixedNow),
            NullLogger<HttpNewsArticleContentReader>.Instance,
            resolveHostAddresses: (_, _) => Task.FromResult(resolvesTo ?? [PublicIp]));

    // -------------------------------------------------------------------------------------------------
    // Allowlist + SSRF gates: rejected BEFORE any request (the scripted handler counts requests).
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Registration_EnabledWithEmptyAllowlist_FailsStartup()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddHttpNewsArticleContentReader(new NewsArticleContentReaderOptions
            {
                AllowedDomains = [],
                UserAgent = "RadarResearch contact@example.com",
            }));

        Assert.Contains("AllowedDomains", ex.Message);
    }

    [Fact]
    public void Registration_ContactLessUserAgent_FailsStartup()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddHttpNewsArticleContentReader(new NewsArticleContentReaderOptions
            {
                AllowedDomains = ["example.com"],
                UserAgent = "Radar",
            }));

        Assert.Contains("contact", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://allowed.example.com/a")]                 // non-HTTP scheme
    [InlineData("https://user:pw@allowed.example.com/a")]       // embedded user-info
    [InlineData("https://allowed.example.com:8443/a")]          // non-default port
    [InlineData("http://127.0.0.1/a")]                          // loopback literal
    [InlineData("http://10.1.2.3/a")]                           // RFC1918 private literal
    [InlineData("http://169.254.1.1/a")]                        // link-local literal
    [InlineData("http://100.64.0.1/a")]                         // CGNAT literal
    [InlineData("http://[::1]/a")]                              // IPv6 loopback literal
    [InlineData("http://[fe80::1]/a")]                          // IPv6 link-local literal
    [InlineData("http://localhost/a")]                          // internal host name
    [InlineData("http://intranet.local/a")]
    public async Task FetchAsync_UnsafeDestination_FailsBeforeAnyRequest(string url)
    {
        var handler = new ScriptedHandler();
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync(url, CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.UnsafeUrl, result.Outcome);
        Assert.Null(result.BodyText);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_UnparseableUrl_IsUnresolvedLandingUrl()
    {
        var handler = new ScriptedHandler();
        var result = await CreateReader(handler).FetchAsync("not a url", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.UnresolvedLandingUrl, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_HostNotOnAllowlist_IsDomainNotAllowed_AndNeverResolvedOrRequested()
    {
        var handler = new ScriptedHandler();
        var resolved = false;
        var reader = new HttpNewsArticleContentReader(
            new HttpClient(handler),
            Options("allowed.example.com"),
            new FixedTime(FixedNow),
            NullLogger<HttpNewsArticleContentReader>.Instance,
            resolveHostAddresses: (_, _) =>
            {
                resolved = true;
                return Task.FromResult(new[] { PublicIp });
            });

        var result = await reader.FetchAsync("https://other.example.org/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.DomainNotAllowed, result.Outcome);
        Assert.Null(result.BodyText);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(resolved); // the allowlist gate precedes DNS by design
    }

    [Fact]
    public async Task FetchAsync_AllowlistedHostResolvingToPrivateAddress_IsUnsafeUrl()
    {
        // DNS-rebinding shape: the operator allowlisted the name, but it resolves (partly) privately.
        var handler = new ScriptedHandler();
        var reader = CreateReader(
            handler, resolvesTo: [PublicIp, IPAddress.Parse("192.168.1.10")]);

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.UnsafeUrl, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    // -------------------------------------------------------------------------------------------------
    // Redirects: every hop re-validated; the hop budget is explicit.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FetchAsync_RedirectToPrivateAddress_IsUnsafeUrl_WithoutRequestingTheTarget()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetRedirect("https://allowed.example.com/story", "http://10.0.0.5/internal");
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.UnsafeUrl, result.Outcome);
        // robots + the first request happened; the private target was NEVER requested.
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("10.0.0.5"));
    }

    [Fact]
    public async Task FetchAsync_RedirectToNonAllowlistedDomain_IsDomainNotAllowed()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetRedirect("https://allowed.example.com/story", "https://elsewhere.example.org/story");
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.DomainNotAllowed, result.Outcome);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("elsewhere"));
    }

    [Fact]
    public async Task FetchAsync_MoreThanFiveHops_IsRedirectLimit()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        for (var i = 0; i < 7; i++)
        {
            handler.SetRedirect(
                $"https://allowed.example.com/hop{i}", $"https://allowed.example.com/hop{i + 1}");
        }

        var reader = CreateReader(handler);
        var result = await reader.FetchAsync("https://allowed.example.com/hop0", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.RedirectLimit, result.Outcome);
        Assert.Equal(HttpNewsArticleContentReader.MaxRedirectHops, result.RedirectHops);
    }

    // -------------------------------------------------------------------------------------------------
    // robots.txt, paywalls, rate limits, bounds, content types — exact outcomes, no stored body.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FetchAsync_RobotsDisallows_IsRobotsDisallowed_WithoutRequestingThePage()
    {
        var handler = new ScriptedHandler();
        handler.SetResponse(
            "https://allowed.example.com/robots.txt",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("User-agent: *\nDisallow: /story", Encoding.UTF8, "text/plain"),
            });
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story/1", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.RobotsDisallowed, result.Outcome);
        Assert.Null(result.BodyText);
        Assert.Single(handler.RequestedUrls); // robots only — the page itself was never requested
    }

    [Fact]
    public async Task FetchAsync_RobotsUnavailable_FailsClosedAsDisallowed()
    {
        var handler = new ScriptedHandler();
        handler.SetResponse(
            "https://allowed.example.com/robots.txt",
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        // "cannot tell whether we may fetch" must never read as permission.
        Assert.Equal(NewsArticleFetchOutcome.RobotsDisallowed, result.Outcome);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    public async Task FetchAsync_AccessDenied_IsPaywalled_NoBodyStored(int status)
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetResponse(
            "https://allowed.example.com/story",
            new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("<html>subscribe to read</html>", Encoding.UTF8, "text/html"),
            });
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.Paywalled, result.Outcome);
        Assert.Equal(status, result.HttpStatus);
        Assert.Null(result.BodyText);
    }

    [Fact]
    public async Task FetchAsync_Http429_IsRateLimited()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetResponse(
            "https://allowed.example.com/story",
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.RateLimited, result.Outcome);
        Assert.Null(result.BodyText);
    }

    [Fact]
    public async Task FetchAsync_OwnDeadlineElapsed_IsTimeout()
    {
        var handler = new HangingHandler();
        var reader = new HttpNewsArticleContentReader(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new NewsArticleContentReaderOptions
            {
                AllowedDomains = ["allowed.example.com"],
                UserAgent = "RadarResearch contact@example.com",
                PerHostInterval = TimeSpan.Zero,
                Timeout = TimeSpan.FromMilliseconds(50),
            },
            new FixedTime(FixedNow),
            NullLogger<HttpNewsArticleContentReader>.Instance,
            resolveHostAddresses: (_, _) => Task.FromResult(new[] { PublicIp }));

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.Timeout, result.Outcome);
        Assert.Null(result.BodyText);
    }

    [Fact]
    public async Task FetchAsync_OversizedBody_IsTooLarge_NoBodyStored()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetResponse(
            "https://allowed.example.com/story",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 4096), Encoding.UTF8, "text/html"),
            });
        var reader = new HttpNewsArticleContentReader(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new NewsArticleContentReaderOptions
            {
                AllowedDomains = ["allowed.example.com"],
                UserAgent = "RadarResearch contact@example.com",
                PerHostInterval = TimeSpan.Zero,
                MaxResponseBytes = 1024,
            },
            new FixedTime(FixedNow),
            NullLogger<HttpNewsArticleContentReader>.Instance,
            resolveHostAddresses: (_, _) => Task.FromResult(new[] { PublicIp }));

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.TooLarge, result.Outcome);
        Assert.Null(result.BodyText);
    }

    [Fact]
    public async Task FetchAsync_UnsupportedContentType_IsTyped_NoBodyStored()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetResponse(
            "https://allowed.example.com/story.pdf",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
                {
                    Headers = { ContentType = new("application/pdf") },
                },
            });
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story.pdf", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.UnsupportedContentType, result.Outcome);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Null(result.BodyText);
    }

    [Fact]
    public async Task FetchAsync_EmptyVisibleText_IsExtractionEmpty()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetResponse(
            "https://allowed.example.com/story",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><script>var x=1;</script><style>.a{}</style></html>", Encoding.UTF8, "text/html"),
            });
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.ExtractionEmpty, result.Outcome);
        Assert.Null(result.BodyText);
    }

    [Fact]
    public async Task FetchAsync_AllowlistedTextualPage_IsFetched_WithFullAttemptProvenance()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetRedirect("https://allowed.example.com/landing", "https://allowed.example.com/story");
        handler.SetResponse(
            "https://allowed.example.com/story",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><nav>menu</nav><body><p>Rocket Lab announced a new  launch.</p></body></html>",
                    Encoding.UTF8,
                    "text/html"),
            });
        var reader = CreateReader(handler);

        var result = await reader.FetchAsync("https://allowed.example.com/landing", CancellationToken.None);

        Assert.Equal(NewsArticleFetchOutcome.Fetched, result.Outcome);
        Assert.Equal("Rocket Lab announced a new launch.", result.BodyText);
        Assert.Equal(FixedNow, result.RetrievedAtUtc);
        Assert.Equal(1, result.RedirectHops);
        Assert.Equal("https://allowed.example.com/story", result.ResolvedUrl);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("text/html", result.ContentType);
        Assert.False(result.Truncated);
        Assert.Equal("news-text-v1", result.ExtractorVersion);
        Assert.NotNull(result.ContentHash);
        Assert.Equal(64, result.ContentHash!.Length); // SHA-256 hex over the extracted text
        Assert.StartsWith("news-fetch-v1;extractor=news-text-v1;domains=sha256:", result.RetrievalPolicy);
    }

    [Fact]
    public async Task FetchAsync_SendsTheContactBearingUserAgent_OnEveryRequestIncludingRobots()
    {
        var handler = new ScriptedHandler();
        handler.SetRobotsAllowAll("allowed.example.com");
        handler.SetResponse(
            "https://allowed.example.com/story",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<p>text</p>", Encoding.UTF8, "text/html"),
            });
        var reader = CreateReader(handler);

        await reader.FetchAsync("https://allowed.example.com/story", CancellationToken.None);

        Assert.Equal(2, handler.UserAgents.Count);
        Assert.All(handler.UserAgents, ua => Assert.Contains("contact@example.com", ua));
    }

    // -------------------------------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------------------------------

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public int RequestCount { get; private set; }

        public List<string> RequestedUrls { get; } = [];

        public List<string> UserAgents { get; } = [];

        public void SetResponse(string url, HttpResponseMessage response) =>
            _responses[url] = () => response;

        public void SetRedirect(string url, string location) =>
            _responses[url] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(location);
                return response;
            };

        public void SetRobotsAllowAll(string host) =>
            SetResponse(
                $"https://{host}/robots.txt",
                new HttpResponseMessage(HttpStatusCode.NotFound));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri!.AbsoluteUri;
            RequestedUrls.Add(url);
            UserAgents.Add(string.Join(" ", request.Headers.GetValues("User-Agent")));

            return Task.FromResult(_responses.TryGetValue(url, out var factory)
                ? factory()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
