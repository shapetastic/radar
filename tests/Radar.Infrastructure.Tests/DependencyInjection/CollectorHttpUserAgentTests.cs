using Microsoft.Extensions.DependencyInjection;

using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Fda;
using Radar.Infrastructure.Patents;
using Radar.Infrastructure.Rss;
using Radar.Infrastructure.Sources;
using Radar.Infrastructure.Trademarks;

namespace Radar.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// The typed clients that identify Radar with a generic User-Agent must all send it, and all send the SAME
/// one. The RSS client is the case that bit: it was registered bare, and .NET's <c>HttpClient</c> sends NO
/// <c>User-Agent</c> by default, so Energy Recovery's press-release feed answered every Radar request with
/// HTTP 403 while answering the identical request carrying any UA at all with HTTP 200.
/// </summary>
public sealed class CollectorHttpUserAgentTests
{
    /// <summary>
    /// Materializes a typed client's configured <c>HttpClient</c>. <c>AddHttpClient&lt;TClient, TImpl&gt;()</c>
    /// names the client after <c>TClient</c>'s short type name, so <c>nameof</c> is the name the factory
    /// registered under. A wrong name would yield an unconfigured client with an EMPTY User-Agent, so these
    /// assertions cannot pass vacuously.
    /// </summary>
    private static HttpClient TypedClient(IServiceProvider provider, string typedClientName) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(typedClientName);

    [Fact]
    public void RssPressReleaseCollector_TypedClient_SendsRadarUserAgent()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddRssPressReleaseCollector()
            .BuildServiceProvider();

        using var client = TypedClient(provider, nameof(IRssFeedReader));

        var userAgent = client.DefaultRequestHeaders.UserAgent.ToString();
        Assert.False(string.IsNullOrWhiteSpace(userAgent));
        Assert.Equal(RadarHttpUserAgent.Default, userAgent);
    }

    [Fact]
    public async Task RssFeedReader_ResolvedFromContainer_SendsUserAgentOnTheWire()
    {
        // The end-to-end guard: it is the header on the ACTUAL outbound request that Energy Recovery's host
        // rejects, so assert on a captured request rather than on registration state alone.
        var capture = new CapturingHandler();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddRssPressReleaseCollector()
            .AddHttpClient<IRssFeedReader, HttpRssFeedReader>()
            .ConfigurePrimaryHttpMessageHandler(() => capture)
            .Services
            .BuildServiceProvider();

        var reader = provider.GetRequiredService<IRssFeedReader>();
        await reader.ReadAsync("https://ir.acme.test/press-releases/rss", CancellationToken.None);

        Assert.NotNull(capture.LastRequest);
        var userAgent = capture.LastRequest!.Headers.UserAgent.ToString();
        Assert.False(string.IsNullOrWhiteSpace(userAgent));
        Assert.Equal(RadarHttpUserAgent.Default, userAgent);
    }

    [Fact]
    public void EveryGenericUserAgentClient_SendsTheSharedConstant()
    {
        // One literal, four call sites: a second copy is how the four silently drift apart (only one copy
        // gets the next edit), so pin that they all resolve to the shared constant.
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddRssPressReleaseCollector()
            .AddPatentActivityCollector(new PatentCollectorOptions())
            .AddFdaClearanceCollector(new FdaCollectorOptions())
            .AddTrademarkActivityCollector(new TrademarkCollectorOptions())
            .BuildServiceProvider();

        string[] typedClientNames =
        [
            nameof(IRssFeedReader),
            nameof(IPatentSearchReader),
            nameof(IFdaClearanceReader),
            nameof(ITrademarkSearchReader),
        ];

        foreach (var name in typedClientNames)
        {
            using var client = TypedClient(provider, name);
            Assert.Equal(RadarHttpUserAgent.Default, client.DefaultRequestHeaders.UserAgent.ToString());
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent(string.Empty),
            });
        }
    }
}
