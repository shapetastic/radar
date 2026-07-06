using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class HttpSec13DGReaderTests
{
    private const string SubmissionsUrl = "https://data.sec.gov/submissions/CIK0001049521.json";
    private const string ArchiveBase = "https://www.sec.gov/Archives/edgar/data/1049521";

    // Columnar submissions fixture: a mix of forms. Only the four beneficial-ownership forms are parsed; the
    // 8-K and Form 4 rows must be ignored. v1 is metadata-only, so NO per-filing body fetch is expected.
    private const string MixedSubmissions = """
        {
          "cik": "1049521",
          "name": "MERCURY SYSTEMS INC",
          "filings": {
            "recent": {
              "form": ["SC 13D", "8-K", "SC 13G", "SC 13D/A", "4", "SC 13G/A"],
              "filingDate": ["2026-06-06", "2026-06-05", "2026-06-04", "2026-06-03", "2026-06-02", "2026-06-01"],
              "acceptanceDateTime": ["2026-06-06T20:00:00.000Z", "2026-06-05T16:30:00.000Z", "2026-06-04T18:00:00.000Z", "2026-06-03T18:00:00.000Z", "2026-06-02T17:05:00.000Z", "2026-06-01T09:00:00.000Z"],
              "accessionNumber": ["0001049521-26-000040", "0001049521-26-000011", "0001049521-26-000041", "0001049521-26-000042", "0001049521-26-000009", "0001049521-26-000043"],
              "primaryDocument": ["sc13d.htm", "mrcy-8k.htm", "sc13g.htm", "sc13da.htm", "form4.xml", "sc13ga.htm"]
            }
          }
        }
        """;

    private static HttpSec13DGReader CreateReader(HttpMessageHandler handler, int maxFilings = 20) =>
        new(
            new HttpClient(handler),
            NullLogger<HttpSec13DGReader>.Instance,
            new Sec13DGCollectorOptions { UserAgent = "Radar Research test@example.com", MaxFilingsPerCompany = maxFilings });

    [Fact]
    public async Task ReadAsync_MixedForms_ParsesOnly13DG_ClassifiesEach_NoBodyFetch()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, MixedSubmissions);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Success, result.Outcome);
        Assert.Equal(4, result.Items.Count); // SC 13D, SC 13G, SC 13D/A, SC 13G/A — not 8-K, not 4

        var d = Assert.Single(result.Items, i => i.Form == "SC 13D");
        Assert.Equal(Sec13DGCategory.Activist13D, d.Category);
        Assert.Equal($"{ArchiveBase}/000104952126000040/0001049521-26-000040-index.htm", d.IndexUrl);

        var g = Assert.Single(result.Items, i => i.Form == "SC 13G");
        Assert.Equal(Sec13DGCategory.Passive13G, g.Category);

        Assert.Equal(Sec13DGCategory.Amendment, Assert.Single(result.Items, i => i.Form == "SC 13D/A").Category);
        Assert.Equal(Sec13DGCategory.Amendment, Assert.Single(result.Items, i => i.Form == "SC 13G/A").Category);

        // v1 is metadata-only: only the submissions JSON is fetched, never a filing body/index page.
        Assert.Equal([SubmissionsUrl], handler.RequestedUrls);
    }

    [Fact]
    public async Task ReadAsync_ExcludesNon13DGForms()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, MixedSubmissions);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.Form is "8-K" or "4");
    }

    [Fact]
    public async Task ReadAsync_HonoursMaxFilings_NewestFirst()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, MixedSubmissions);

        var result = await CreateReader(handler, maxFilings: 2).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("SC 13D", result.Items[0].Form);   // newest
        Assert.Equal("SC 13G", result.Items[1].Form);
    }

    [Fact]
    public async Task ReadAsync_MalformedSubmissions_ReturnsMalformed()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, "not { json");

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_EmptySubmissions_ReturnsMalformed()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, string.Empty);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_MissingCik_ReturnsMalformed()
    {
        const string noCik = """
            {
              "name": "MERCURY SYSTEMS INC",
              "filings": { "recent": { "form": ["SC 13D"], "filingDate": ["2026-06-02"],
                "acceptanceDateTime": ["2026-06-02T20:00:00.000Z"],
                "accessionNumber": ["0001049521-26-000040"], "primaryDocument": ["sc13d.htm"] } }
            }
            """;
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, noCik);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_MissingFilingsRecent_ReturnsMalformed()
    {
        const string noRecent = """{ "cik": "1049521", "name": "MERCURY SYSTEMS INC" }""";
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, noRecent);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_NoOwnershipForms_ReturnsSuccessEmpty()
    {
        // A valid, well-formed submissions payload with only non-13D/G forms is a quiet issuer (Success), NOT
        // a malformed feed.
        const string onlyOtherForms = """
            {
              "cik": "1049521",
              "filings": { "recent": { "form": ["8-K", "10-Q"], "filingDate": ["2026-06-02", "2026-06-01"],
                "acceptanceDateTime": ["2026-06-02T20:00:00.000Z", "2026-06-01T20:00:00.000Z"],
                "accessionNumber": ["acc-8k", "acc-10q"], "primaryDocument": ["a.htm", "b.htm"] } }
            }
            """;
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, onlyOtherForms);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Success, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_Http403_ReturnsForbidden()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.Forbidden, "forbidden");

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Forbidden, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_SubmissionsTimeout_ReturnsTimeout()
    {
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(Sec13DGReadOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, MixedSubmissions);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateReader(handler).ReadAsync(SubmissionsUrl, cts.Token));
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _byUrl = new(StringComparer.Ordinal);

        public List<string> RequestedUrls { get; } = [];

        public void Add(string url, HttpStatusCode status, string body) => _byUrl[url] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            var (status, body) = _byUrl.TryGetValue(url, out var entry)
                ? entry
                : (HttpStatusCode.NotFound, "not found");

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
}
