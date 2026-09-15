using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Evidence;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

/// <summary>
/// SPEC 228 §1 — the item-1.01 read fetches the filing's REAL primary 8-K document (declared or form-typed), then
/// EX-99.1, then the EX-2.1 merger agreement, each when the index shows one, and never an exhibit posing as the
/// 8-K. Driven by the real index pages in <see cref="RealSecFilingIndexPages"/>.
/// </summary>
public sealed class HttpSecAcquisitionFilingReaderTests
{
    private const string PrimaryHtml = "<html><body><p>UNITED STATES SECURITIES AND EXCHANGE COMMISSION</p><p>FORM 8-K CURRENT REPORT Pursuant to Section 13 OR 15(d)</p><p>Item 1.01 Entry into a Material Definitive Agreement.</p></body></html>";

    private const string Ex991Html = "<html><body><p>Press release body.</p></body></html>";

    private const string CreditAgreementHtml = "<html><body><p>AMENDED AND RESTATED CREDIT AGREEMENT</p></body></html>";

    [Fact]
    public async Task ReadAsync_RealShooIndex_ReadsThe8KThenEx991_NeverTheCreditAgreement()
    {
        var handler = Route(RealSecFilingIndexPages.ShooCreditAgreementAndResults, new()
        {
            ["form8-k.htm"] = PrimaryHtml,
            ["ex99-1.htm"] = Ex991Html,
            ["ex10-1.htm"] = CreditAgreementHtml,
        });

        var body = await Reader(handler).ReadAsync("913241", "0001641172-25-008949", "form8-k.htm", CancellationToken.None);

        Assert.True(body.IsSuccess);
        Assert.StartsWith("UNITED STATES SECURITIES AND EXCHANGE COMMISSION", body.PlainText.TrimStart(), StringComparison.Ordinal);
        Assert.True(
            body.PlainText.IndexOf("CURRENT REPORT", StringComparison.Ordinal)
                < body.PlainText.IndexOf("Press release body.", StringComparison.Ordinal));
        Assert.DoesNotContain("CREDIT AGREEMENT", body.PlainText, StringComparison.Ordinal);
        Assert.Equal(
            [
                "https://www.sec.gov/Archives/edgar/data/913241/000164117225008949/0001641172-25-008949-index.html",
                "https://www.sec.gov/Archives/edgar/data/913241/000164117225008949/form8-k.htm",
                "https://www.sec.gov/Archives/edgar/data/913241/000164117225008949/ex99-1.htm",
            ],
            handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_RealMyrgIndex_WithoutADeclaredPrimary_ReadsTheFormTypedRow_InTwoRequests()
    {
        var handler = Route(RealSecFilingIndexPages.MyrgNoExhibits, new() { ["myrg-20260908.htm"] = PrimaryHtml });

        var body = await Reader(handler).ReadAsync("700923", "0000700923-26-000047", primaryDocument: null, CancellationToken.None);

        Assert.True(body.IsSuccess);
        Assert.Contains("Item 1.01", body.PlainText, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requested.Count);
    }

    [Fact]
    public async Task ReadAsync_RealHzoIndex_FormerlyNoPrimaryDocumentRow_NowReadsPrimaryAndEx991()
    {
        var handler = Route(RealSecFilingIndexPages.HzoEx991Only, new()
        {
            ["hzo-20260629.htm"] = PrimaryHtml,
            ["hzo-ex99_1.htm"] = Ex991Html,
        });

        var body = await Reader(handler).ReadAsync("1057060", "0001193125-26-290439", "hzo-20260629.htm", CancellationToken.None);

        Assert.True(body.IsSuccess);
        Assert.Contains("Press release body.", body.PlainText, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requested.Count);
    }

    [Fact]
    public async Task ReadAsync_RealHzoMergerIndex_ReadsPrimaryThenEx991ThenTheEx21Agreement_WhateverTheIndexOrder()
    {
        var handler = Route(RealSecFilingIndexPages.HzoMergerAgreement, new()
        {
            ["d135056d8k.htm"] = PrimaryHtml,
            ["d135056dex991.htm"] = Ex991Html,
            ["d135056dex21.htm"] = "<html><body><p>AGREEMENT AND PLAN OF MERGER by and among SHM Holdco, LLC</p></body></html>",
        });

        var body = await Reader(handler).ReadAsync("1057060", "0001193125-26-341302", "d135056d8k.htm", CancellationToken.None);

        Assert.True(body.IsSuccess);
        var cover = body.PlainText.IndexOf("CURRENT REPORT", StringComparison.Ordinal);
        var release = body.PlainText.IndexOf("Press release body.", StringComparison.Ordinal);
        var agreement = body.PlainText.IndexOf("AGREEMENT AND PLAN OF MERGER", StringComparison.Ordinal);
        Assert.True(cover >= 0 && cover < release && release < agreement, $"order cover {cover} < release {release} < agreement {agreement}");
        Assert.Equal(
            [
                "https://www.sec.gov/Archives/edgar/data/1057060/000119312526341302/0001193125-26-341302-index.html",
                "https://www.sec.gov/Archives/edgar/data/1057060/000119312526341302/d135056d8k.htm",
                "https://www.sec.gov/Archives/edgar/data/1057060/000119312526341302/d135056dex991.htm",
                "https://www.sec.gov/Archives/edgar/data/1057060/000119312526341302/d135056dex21.htm",
            ],
            handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_Ex21FetchFails_FailsTheWholeRead()
    {
        var handler = Route(RealSecFilingIndexPages.HzoMergerAgreement, new()
        {
            ["d135056d8k.htm"] = PrimaryHtml,
            ["d135056dex991.htm"] = Ex991Html,
        });

        var body = await Reader(handler).ReadAsync("1057060", "0001193125-26-341302", "d135056d8k.htm", CancellationToken.None);

        Assert.False(body.IsSuccess);
        Assert.Equal("HTTP 404", body.Detail);
    }

    [Fact]
    public async Task ReadAsync_NoAuthoritativePrimary_IsANamedFailure_AndFetchesNothingButTheIndex()
    {
        const string index = """
            <table class="tableFile">
              <tr><th>Seq</th><th>Description</th><th>Document</th><th>Type</th><th>Size</th></tr>
              <tr><td>1</td><td>EX-10.1</td><td><a href="/Archives/edgar/data/1/000000000126000001/ex10-1.htm">ex10-1.htm</a></td><td>EX-10.1</td><td>9</td></tr>
              <tr><td>2</td><td>EX-99.1</td><td><a href="/Archives/edgar/data/1/000000000126000001/ex99-1.htm">ex99-1.htm</a></td><td>EX-99.1</td><td>9</td></tr>
            </table>
            """;
        var handler = Route(index, new() { ["ex10-1.htm"] = CreditAgreementHtml, ["ex99-1.htm"] = Ex991Html });

        var body = await Reader(handler).ReadAsync("1", "0000000001-26-000001", "missing.htm", CancellationToken.None);

        Assert.False(body.IsSuccess);
        Assert.Equal(SecFilingIndexTable.NoPrimaryDocumentRow, body.Detail);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task ReadAsync_Ex991FetchFails_FailsTheWholeRead()
    {
        var handler = Route(RealSecFilingIndexPages.ShooCreditAgreementAndResults, new() { ["form8-k.htm"] = PrimaryHtml });

        var body = await Reader(handler).ReadAsync("913241", "0001641172-25-008949", "form8-k.htm", CancellationToken.None);

        Assert.False(body.IsSuccess);
        Assert.Equal("HTTP 404", body.Detail);
    }

    private static HttpSecAcquisitionFilingReader Reader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new EvidenceNormalizer(), NullLogger<HttpSecAcquisitionFilingReader>.Instance);

    private static RoutingHandler Route(string index, Dictionary<string, string> documents) =>
        new(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("-index.html", StringComparison.Ordinal))
            {
                return Html(HttpStatusCode.OK, index);
            }

            var file = url[(url.LastIndexOf('/') + 1)..];
            return documents.TryGetValue(file, out var html)
                ? Html(HttpStatusCode.OK, html)
                : Html(HttpStatusCode.NotFound, "missing");
        });

    private static HttpResponseMessage Html(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(route(request));
        }
    }
}
