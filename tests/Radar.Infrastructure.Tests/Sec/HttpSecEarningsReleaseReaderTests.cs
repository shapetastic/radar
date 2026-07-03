using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Evidence;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class HttpSecEarningsReleaseReaderTests
{
    private const string Cik = "1049521";
    private const string Accession = "0001049521-26-000021";

    private const string BaseUrl =
        "https://www.sec.gov/Archives/edgar/data/1049521/000104952126000021";

    private const string IndexUrl = BaseUrl + "/0001049521-26-000021-index.html";

    private const string EightKFile = "mrcy-20260505.htm";
    private const string Ex991File = "a2026q3earningsreleaseex.htm";
    private const string Ex992File = "q3fy26earningspresentati.htm";
    private const string Ex993File = "q3fy26supplementaldata.htm";

    // Models the real SEC -index.html document table: columns Seq, Description, Document (anchored .htm),
    // Type, Size. Three rows: the boilerplate 8-K cover page, the EX-99.1 earnings release, and the EX-99.2
    // slide deck.
    private static readonly string IndexWith991 = BuildIndex(
    [
        ("1", "mrcy-20260505.htm document", EightKFile, "8-K", "38 KB"),
        ("2", "a2026q3earningsreleaseex.htm document", Ex991File, "EX-99.1", "321 KB"),
        ("3", "q3fy26earningspresentati.htm document", Ex992File, "EX-99.2", "150 KB"),
    ]);

    // No exact EX-99.1 row — only the 8-K cover and the EX-99.2 deck; the reader must fall back to EX-99.2.
    private static readonly string IndexWithFallback = BuildIndex(
    [
        ("1", "mrcy-20260505.htm document", EightKFile, "8-K", "38 KB"),
        ("2", "q3fy26earningspresentati.htm document", Ex992File, "EX-99.2", "150 KB"),
    ]);

    // No exact EX-99.1 row, but two EX-99.* exhibits: the larger EX-99.3 (500 KB) appears *after* the smaller
    // EX-99.2 (150 KB) in document order, so selecting it proves the fallback tie-break is largest-by-Size and
    // not just first-in-order.
    private static readonly string IndexWithMultipleEx99 = BuildIndex(
    [
        ("1", "mrcy-20260505.htm document", EightKFile, "8-K", "38 KB"),
        ("2", "q3fy26earningspresentati.htm document", Ex992File, "EX-99.2", "150 KB"),
        ("3", "q3fy26supplementaldata.htm document", Ex993File, "EX-99.3", "500 KB"),
    ]);

    // Only the 8-K cover page (plus a non-.htm EX-101 XBRL row that is not a document candidate) — no EX-99.*.
    private static readonly string IndexNoEx99 = BuildIndex(
    [
        ("1", "mrcy-20260505.htm document", EightKFile, "8-K", "38 KB"),
        ("2", "R1.xml document", "mrcy-20260505_htm.xml", "EX-101.INS", "12 KB"),
    ]);

    private const string Ex991Html = """
        <html>
          <head><title>Press Release</title><style>.x{color:red}</style></head>
          <body>
            <h1>Mercury Systems Reports Third Quarter Fiscal 2026 Results</h1>
            <p>Record Q3 FY26 Bookings of $348&nbsp;million grew 73.7% year-over-year.</p>
            <p>Revenue of $236 million, up 11.5% organically.</p>
          </body>
        </html>
        """;

    private static HttpSecEarningsReleaseReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new EvidenceNormalizer(), NullLogger<HttpSecEarningsReleaseReader>.Instance);

    [Fact]
    public async Task ReadAsync_SelectsEx991_ReturnsStrippedPlainText()
    {
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWith991);
            if (url.EndsWith(Ex991File, StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, Ex991Html);
            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal("EX-99.1", result.DocumentType);
        Assert.Equal(Ex991File, result.DocumentFileName);
        Assert.Contains("Record Q3 FY26 Bookings", result.PlainText);
        Assert.Contains("Revenue of $236 million", result.PlainText);
        // Shared stripper removed all markup (and the <style> block) — plain text, no tags.
        Assert.DoesNotContain("<", result.PlainText);
        Assert.DoesNotContain("color:red", result.PlainText);

        // The reader fetched the EX-99.1 exhibit URL (not the 8-K), after the index.
        Assert.Contains(BaseUrl + "/" + Ex991File, handler.Requested);
        Assert.DoesNotContain(BaseUrl + "/" + EightKFile, handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_NoEx99Row_ReturnsNoEarningsExhibit_AndDoesNotFetch8K()
    {
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexNoEx99);
            return Html(HttpStatusCode.OK, "should not be fetched");
        });
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.NoEarningsExhibit, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.PlainText);
        // Only the index was fetched — the primary 8-K document must never be a fallback.
        Assert.Equal([IndexUrl], handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_NoExactEx991_FallsBackToEx99Star()
    {
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWithFallback);
            if (url.EndsWith(Ex992File, StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, "<html><body><p>Slide deck body.</p></body></html>");
            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Success, result.Outcome);
        Assert.Equal("EX-99.2", result.DocumentType);
        Assert.Equal(Ex992File, result.DocumentFileName);
        Assert.Contains("Slide deck body.", result.PlainText);
        Assert.Contains(BaseUrl + "/" + Ex992File, handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_MultipleEx99NoExact_SelectsLargestBySize()
    {
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWithMultipleEx99);
            if (url.EndsWith(Ex993File, StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, "<html><body><p>Supplemental body.</p></body></html>");
            return Html(HttpStatusCode.NotFound, "missing");
        });
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Success, result.Outcome);
        // EX-99.3 (500 KB) wins over EX-99.2 (150 KB) despite coming later in document order — size, not order.
        Assert.Equal("EX-99.3", result.DocumentType);
        Assert.Equal(Ex993File, result.DocumentFileName);
        Assert.Contains("Supplemental body.", result.PlainText);
        // The larger exhibit was fetched; the smaller EX-99.2 was not.
        Assert.Contains(BaseUrl + "/" + Ex993File, handler.Requested);
        Assert.DoesNotContain(BaseUrl + "/" + Ex992File, handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_IndexHttp403_ReturnsForbiddenWithoutThrowing()
    {
        var handler = new RoutingHandler(_ => Html(HttpStatusCode.Forbidden, "forbidden"));
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Forbidden, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ReadAsync_ExhibitHttp403_ReturnsForbiddenWithoutThrowing()
    {
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWith991);
            return Html(HttpStatusCode.Forbidden, "forbidden");
        });
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_ExhibitHttp500_ReturnsHttpError()
    {
        // A non-403 non-success status (here 500 on the exhibit fetch) maps to the generic HttpError outcome,
        // distinct from the dedicated Forbidden path.
        var handler = new RoutingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
                return Html(HttpStatusCode.OK, IndexWith991);
            return Html(HttpStatusCode.InternalServerError, "server error");
        });
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.HttpError, result.Outcome);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ReadAsync_EmptyIndexBody_ReturnsMalformed()
    {
        var handler = new RoutingHandler(_ => Html(HttpStatusCode.OK, string.Empty));
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_IndexWithNoDocumentTable_ReturnsMalformed()
    {
        var handler = new RoutingHandler(_ =>
            Html(HttpStatusCode.OK, "<html><body><p>No document table here.</p></body></html>"));
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_TransportError_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync(Cik, Accession, CancellationToken.None);

        Assert.Equal(SecEarningsReleaseReadOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new RoutingHandler(_ => Html(HttpStatusCode.OK, IndexWith991)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(Cik, Accession, cts.Token));
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

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
