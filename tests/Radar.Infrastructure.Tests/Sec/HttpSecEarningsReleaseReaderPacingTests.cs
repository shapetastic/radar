using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Evidence;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

/// <summary>
/// Covers the spec-107 polite inter-request pacing: the reader waits so successive www.sec.gov requests are at
/// least <see cref="SecEarningsReleaseReaderOptions.MinRequestInterval"/> apart, driven by the injected
/// <see cref="TimeProvider"/>. A <see cref="FakeTimeProvider"/> makes the pacing deterministic and offline (no
/// real waiting); a <see cref="TimeSpan.Zero"/> interval restores the un-paced behaviour.
/// </summary>
public sealed class HttpSecEarningsReleaseReaderPacingTests
{
    private const string Cik = "1049521";
    private const string Accession = "0001049521-26-000021";

    private const string BaseUrl =
        "https://www.sec.gov/Archives/edgar/data/1049521/000104952126000021";

    private const string EightKFile = "mrcy-20260505.htm";
    private const string Ex991File = "a2026q3earningsreleaseex.htm";

    private static readonly string IndexWith991 = BuildIndex(
    [
        ("1", "mrcy-20260505.htm document", EightKFile, "8-K", "38 KB"),
        ("2", "a2026q3earningsreleaseex.htm document", Ex991File, "EX-99.1", "321 KB"),
    ]);

    private const string Ex991Html =
        "<html><body><h1>Results</h1><p>Record Q3 FY26 Bookings of $348 million.</p></body></html>";

    private static HttpSecEarningsReleaseReader CreateReader(
        HttpMessageHandler handler, TimeSpan minInterval, TimeProvider timeProvider) =>
        new(
            new HttpClient(handler),
            new EvidenceNormalizer(),
            new SecEarningsReleaseReaderOptions { MinRequestInterval = minInterval },
            timeProvider,
            NullLogger<HttpSecEarningsReleaseReader>.Instance);

    [Fact]
    public async Task ReadAsync_PacesExhibitFetchByMinRequestInterval()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWith991);
            if (url.EndsWith(Ex991File, StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, Ex991Html);
            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(handler, TimeSpan.FromMilliseconds(250), fake);

        // The index fetch happens immediately (no prior request), but the exhibit fetch must await the pacing
        // delay — the task does NOT complete until the fake clock advances by the interval.
        var task = reader.ReadAsync(Cik, Accession, CancellationToken.None);
        Assert.False(task.IsCompleted);

        fake.Advance(TimeSpan.FromMilliseconds(250));

        var result = await task;
        Assert.Equal(SecEarningsReleaseReadOutcome.Success, result.Outcome);
        Assert.Contains(BaseUrl + "/" + Ex991File, handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_ZeroInterval_CompletesWithoutAdvancingClock()
    {
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWith991);
            if (url.EndsWith(Ex991File, StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, Ex991Html);
            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(handler, TimeSpan.Zero, fake);

        // Zero interval ⇒ no pacing delay ⇒ ReadAsync completes without ever advancing the fake clock.
        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);
        Assert.Equal(SecEarningsReleaseReadOutcome.Success, result.Outcome);
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

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(_route(request));
        }
    }
}
