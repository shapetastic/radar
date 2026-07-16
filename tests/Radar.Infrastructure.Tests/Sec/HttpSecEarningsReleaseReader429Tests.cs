using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Evidence;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

/// <summary>
/// Covers the bounded HTTP 429 backoff-retry the earnings-release reader owns (spec 105): SEC 429s the burst of
/// <c>www.sec.gov/Archives</c> requests this reader fires, so a transient throttle must be retried (not skipped)
/// before it starves the AI directional path. All tests use <see cref="TimeSpan.Zero"/> backoff so the retry
/// path stays instant and offline (except the cancellation test, which needs a real delay to interrupt).
/// </summary>
public sealed class HttpSecEarningsReleaseReader429Tests
{
    private const string Cik = "1049521";
    private const string Accession = "0001049521-26-000021";

    private const string BaseUrl =
        "https://www.sec.gov/Archives/edgar/data/1049521/000104952126000021";

    private const string IndexUrl = BaseUrl + "/0001049521-26-000021-index.html";

    private const string EightKFile = "mrcy-20260505.htm";
    private const string Ex991File = "a2026q3earningsreleaseex.htm";

    // A minimal valid -index.html document table the reader's ParseDocumentTable/SelectEarningsExhibit accept:
    // the boilerplate 8-K cover row plus one EX-99.1 earnings-release row linking a .htm document.
    private static readonly string IndexWith991 = BuildIndex(
    [
        ("1", "mrcy-20260505.htm document", EightKFile, "8-K", "38 KB"),
        ("2", "a2026q3earningsreleaseex.htm document", Ex991File, "EX-99.1", "321 KB"),
    ]);

    private const string Ex991Html =
        "<html><body><h1>Results</h1><p>Record Q3 FY26 Bookings of $348 million.</p></body></html>";

    private static HttpSecEarningsReleaseReader CreateReader(
        HttpMessageHandler handler, SecEarningsReleaseReaderOptions options) =>
        new(
            new HttpClient(handler),
            new EvidenceNormalizer(),
            options,
            NullLogger<HttpSecEarningsReleaseReader>.Instance);

    [Fact]
    public async Task ReadAsync_429ThenValidExhibit_RetriesAndSucceeds()
    {
        // The index fetch always succeeds; the exhibit fetch 429s once, then returns a valid EX-99.1 body.
        var exhibitCalls = 0;
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWith991);
            if (url.EndsWith(Ex991File, StringComparison.Ordinal))
                return ++exhibitCalls == 1
                    ? Html(HttpStatusCode.TooManyRequests, "rate limited")
                    : Html(HttpStatusCode.OK, Ex991Html);
            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(
            handler, new SecEarningsReleaseReaderOptions { MaxRetriesOn429 = 2, RetryBackoff = TimeSpan.Zero });

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Success, result.Outcome);
        Assert.Equal("EX-99.1", result.DocumentType);
        Assert.Contains("Record Q3 FY26 Bookings", result.PlainText);
        // Exactly the 429 attempt + one retry that succeeded; bounded by 1 + MaxRetriesOn429.
        Assert.Equal(2, exhibitCalls);
    }

    [Fact]
    public async Task ReadAsync_429OnEveryExhibitFetch_ReturnsRateLimitedAfterExactRetries()
    {
        var exhibitCalls = 0;
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWith991);
            if (url.EndsWith(Ex991File, StringComparison.Ordinal))
            {
                exhibitCalls++;
                return Html(HttpStatusCode.TooManyRequests, "rate limited");
            }

            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(
            handler, new SecEarningsReleaseReaderOptions { MaxRetriesOn429 = 2, RetryBackoff = TimeSpan.Zero });

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.RateLimited, result.Outcome);
        Assert.False(result.IsSuccess);
        // Initial attempt + exactly MaxRetriesOn429 retries.
        Assert.Equal(3, exhibitCalls);
    }

    [Fact]
    public async Task ReadAsync_429_NoRetriesConfigured_SingleAttemptThenRateLimited()
    {
        // MaxRetriesOn429 = 0 restores today's exact single-attempt behaviour (429 -> skip).
        var calls = 0;
        var handler = new RoutingHandler(_ =>
        {
            calls++;
            return Html(HttpStatusCode.TooManyRequests, "rate limited");
        });
        var reader = CreateReader(
            handler, new SecEarningsReleaseReaderOptions { MaxRetriesOn429 = 0, RetryBackoff = TimeSpan.Zero });

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.RateLimited, result.Outcome);
        Assert.Equal(1, calls); // the index fetch only — no retry, no exhibit fetch
    }

    [Fact]
    public async Task ReadAsync_429OnIndexFetch_IsRetriedToo()
    {
        // The 429 hits the INDEX fetch (not just the exhibit); the retry applies to both because it lives inside
        // FetchAsync. Once the index succeeds, the exhibit fetch completes the read.
        var indexCalls = 0;
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return ++indexCalls == 1
                    ? Html(HttpStatusCode.TooManyRequests, "rate limited")
                    : Html(HttpStatusCode.OK, IndexWith991);
            if (url.EndsWith(Ex991File, StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, Ex991Html);
            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(
            handler, new SecEarningsReleaseReaderOptions { MaxRetriesOn429 = 2, RetryBackoff = TimeSpan.Zero });

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Success, result.Outcome);
        Assert.Equal(2, indexCalls); // 429 attempt + successful retry on the index
    }

    [Fact]
    public async Task ReadAsync_CancellationDuringBackoff_Throws_AndStopsRetrying()
    {
        // A non-zero backoff plus a token cancelled the moment the first 429 arrives: the reader's
        // Task.Delay(backoff, ct) must observe cancellation and propagate OperationCanceledException, and no
        // further HTTP attempt is made.
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var handler = new RoutingHandler(_ =>
        {
            calls++;
            cts.Cancel();
            return Html(HttpStatusCode.TooManyRequests, "rate limited");
        });
        var reader = CreateReader(
            handler,
            new SecEarningsReleaseReaderOptions { MaxRetriesOn429 = 2, RetryBackoff = TimeSpan.FromMinutes(1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(Cik, Accession, cts.Token));

        Assert.Equal(1, calls); // the first (index) 429 attempt only — cancellation aborts before any retry
    }

    private static string BuildIndex(
        IReadOnlyList<(string Seq, string Description, string File, string Type, string Size)> rows)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <html><body>
            <table class="tableFile" summary="Document Format Files">
              <tr>
                <th scope="col">Seq</th><th scope="col">Description</th>
                <th scope="col">Document</th><th scope="col">Type</th><th scope="col">Size</th>
              </tr>
            """);
        foreach (var (seq, description, file, type, size) in rows)
        {
            var href = "/Archives/edgar/data/1049521/000104952126000021/" + file;
            sb.Append(
                $"""
                  <tr>
                    <td scope="row">{seq}</td>
                    <td scope="row">{description}</td>
                    <td scope="row"><a href="{href}">{file}</a></td>
                    <td scope="row">{type}</td>
                    <td scope="row">{size}</td>
                  </tr>
                """);
        }

        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    private static HttpResponseMessage Html(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route = route;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_route(request));
        }
    }
}
