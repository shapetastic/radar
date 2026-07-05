using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Signals;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class HttpSecForm4ReaderTests
{
    private const string SubmissionsUrl = "https://data.sec.gov/submissions/CIK0001049521.json";
    private const string ArchiveBase = "https://www.sec.gov/Archives/edgar/data/1049521";

    // Columnar submissions fixture: a mix of forms; only form == "4" rows are parsed. Two Form 4s: one with
    // an XSL-rendered primaryDocument (xslF345X06/primary_01.xml -> strip to primary_01.xml) and one already
    // bare (form4.xml). The 8-K and 10-Q rows must be ignored.
    private const string MixedSubmissions = """
        {
          "cik": "1049521",
          "name": "MERCURY SYSTEMS INC",
          "filings": {
            "recent": {
              "form": ["4", "8-K", "4", "10-Q"],
              "filingDate": ["2026-06-02", "2026-06-01", "2026-05-20", "2026-05-01"],
              "acceptanceDateTime": ["2026-06-02T20:00:00.000Z", "2026-06-01T16:30:00.000Z", "2026-05-20T18:00:00.000Z", "2026-05-01T17:05:00.000Z"],
              "accessionNumber": ["0001049521-26-000030", "0001049521-26-000011", "0001049521-26-000029", "0001049521-26-000009"],
              "primaryDocument": ["xslF345X06/primary_01.xml", "mrcy-8k.htm", "form4.xml", "mrcy-10q.htm"]
            }
          }
        }
        """;

    // A single Form 4 row, parameterizable accession + primaryDocument.
    private static string SingleForm4Submissions(string accession, string primaryDocument) => $$"""
        {
          "cik": "1049521",
          "filings": {
            "recent": {
              "form": ["4"],
              "filingDate": ["2026-06-02"],
              "acceptanceDateTime": ["2026-06-02T20:00:00.000Z"],
              "accessionNumber": ["{{accession}}"],
              "primaryDocument": ["{{primaryDocument}}"]
            }
          }
        }
        """;

    // --- Form 4 XML fixtures (root <ownershipDocument>, NO namespace) ---

    private static string PurchaseXml(string ticker = "MRCY") => $"""
        <ownershipDocument>
          <documentType>4</documentType>
          <issuer><issuerTradingSymbol>{ticker}</issuerTradingSymbol></issuer>
          <reportingOwner><reportingOwnerId><rptOwnerName>JANE DOE</rptOwnerName></reportingOwnerId></reportingOwner>
          <aff10b5One>false</aff10b5One>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>P</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>1000</value></transactionShares>
                <transactionPricePerShare><value>50</value></transactionPricePerShare>
                <transactionAcquiredDisposedCode><value>A</value></transactionAcquiredDisposedCode>
              </transactionAmounts>
            </nonDerivativeTransaction>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    // Mercury shape: two S (disposed) transactions, aff10b5One = 0 -> Negative.
    private const string SaleXml = """
        <ownershipDocument>
          <documentType>4</documentType>
          <issuer><issuerTradingSymbol>MRCY</issuerTradingSymbol></issuer>
          <reportingOwner><reportingOwnerId><rptOwnerName>JOHN ROE</rptOwnerName></reportingOwnerId></reportingOwner>
          <aff10b5One>0</aff10b5One>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>S</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>8000</value></transactionShares>
                <transactionPricePerShare><value>99</value></transactionPricePerShare>
                <transactionAcquiredDisposedCode><value>D</value></transactionAcquiredDisposedCode>
              </transactionAmounts>
            </nonDerivativeTransaction>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>S</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>1250</value></transactionShares>
                <transactionPricePerShare><value>99</value></transactionPricePerShare>
                <transactionAcquiredDisposedCode><value>D</value></transactionAcquiredDisposedCode>
              </transactionAmounts>
            </nonDerivativeTransaction>
            <nonDerivativeHolding>
              <transactionAmounts><transactionShares><value>50000</value></transactionShares></transactionAmounts>
            </nonDerivativeHolding>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    // Same sale but aff10b5One = true -> plan override -> Neutral.
    private const string PlannedSaleXml = """
        <ownershipDocument>
          <issuer><issuerTradingSymbol>MRCY</issuerTradingSymbol></issuer>
          <reportingOwner><reportingOwnerId><rptOwnerName>JOHN ROE</rptOwnerName></reportingOwnerId></reportingOwner>
          <aff10b5One>true</aff10b5One>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>S</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>8000</value></transactionShares>
                <transactionPricePerShare><value>99</value></transactionPricePerShare>
                <transactionAcquiredDisposedCode><value>D</value></transactionAcquiredDisposedCode>
              </transactionAmounts>
            </nonDerivativeTransaction>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    // Sale with aff10b5One absent but a 10b5-1 footnote -> plan override -> Neutral.
    private const string FootnotePlanSaleXml = """
        <ownershipDocument>
          <issuer><issuerTradingSymbol>MRCY</issuerTradingSymbol></issuer>
          <reportingOwner><reportingOwnerId><rptOwnerName>JOHN ROE</rptOwnerName></reportingOwnerId></reportingOwner>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>S</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>8000</value></transactionShares>
                <transactionPricePerShare><value>99</value></transactionPricePerShare>
                <transactionAcquiredDisposedCode><value>D</value></transactionAcquiredDisposedCode>
              </transactionAmounts>
            </nonDerivativeTransaction>
          </nonDerivativeTable>
          <footnotes><footnote id="F1">Sale made pursuant to a Rule 10b5-1 trading plan.</footnote></footnotes>
        </ownershipDocument>
        """;

    // AEHR grant shape: code A, price 0, aff10b5One false -> Neutral/excluded.
    private const string GrantXml = """
        <ownershipDocument>
          <issuer><issuerTradingSymbol>AEHR</issuerTradingSymbol></issuer>
          <reportingOwner><reportingOwnerId><rptOwnerName>SAM GRANT</rptOwnerName></reportingOwnerId></reportingOwner>
          <aff10b5One>false</aff10b5One>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>A</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>5000</value></transactionShares>
                <transactionPricePerShare><value>0</value></transactionPricePerShare>
                <transactionAcquiredDisposedCode><value>A</value></transactionAcquiredDisposedCode>
              </transactionAmounts>
            </nonDerivativeTransaction>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    private static string SingleCodeXml(string code) => $"""
        <ownershipDocument>
          <reportingOwner><reportingOwnerId><rptOwnerName>SOME OWNER</rptOwnerName></reportingOwnerId></reportingOwner>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>{code}</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>1000</value></transactionShares>
                <transactionPricePerShare><value>25</value></transactionPricePerShare>
              </transactionAmounts>
            </nonDerivativeTransaction>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    // Mixed buy + sell in one filing (no plan) -> Neutral, netValue = max.
    private const string MixedBuySellXml = """
        <ownershipDocument>
          <reportingOwner><reportingOwnerId><rptOwnerName>MIXED OWNER</rptOwnerName></reportingOwnerId></reportingOwner>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>P</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>100</value></transactionShares>
                <transactionPricePerShare><value>10</value></transactionPricePerShare>
              </transactionAmounts>
            </nonDerivativeTransaction>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>S</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>500</value></transactionShares>
                <transactionPricePerShare><value>10</value></transactionPricePerShare>
              </transactionAmounts>
            </nonDerivativeTransaction>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    // Two distinct reporting owners both purchasing -> cluster.
    private const string ClusterPurchaseXml = """
        <ownershipDocument>
          <reportingOwner><reportingOwnerId><rptOwnerName>OWNER ONE</rptOwnerName></reportingOwnerId></reportingOwner>
          <reportingOwner><reportingOwnerId><rptOwnerName>OWNER TWO</rptOwnerName></reportingOwnerId></reportingOwner>
          <nonDerivativeTable>
            <nonDerivativeTransaction>
              <transactionCoding><transactionCode>P</transactionCode></transactionCoding>
              <transactionAmounts>
                <transactionShares><value>1000</value></transactionShares>
                <transactionPricePerShare><value>50</value></transactionPricePerShare>
              </transactionAmounts>
            </nonDerivativeTransaction>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    // Only a holding row (no transaction) -> Neutral, netValue 0.
    private const string HoldingOnlyXml = """
        <ownershipDocument>
          <reportingOwner><reportingOwnerId><rptOwnerName>HOLDER</rptOwnerName></reportingOwnerId></reportingOwner>
          <nonDerivativeTable>
            <nonDerivativeHolding>
              <transactionAmounts><transactionShares><value>12345</value></transactionShares></transactionAmounts>
            </nonDerivativeHolding>
          </nonDerivativeTable>
        </ownershipDocument>
        """;

    private static HttpSecForm4Reader CreateReader(HttpMessageHandler handler, int maxFilings = 15) =>
        new(
            new HttpClient(handler),
            NullLogger<HttpSecForm4Reader>.Instance,
            new SecForm4CollectorOptions { UserAgent = "Radar Research test@example.com", MaxFilingsPerCompany = maxFilings });

    [Fact]
    public async Task ReadAsync_MixedForms_ParsesOnlyForm4_AndStripsXslPathForRawXmlUrl()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, MixedSubmissions);
        // XSL-rendered path must be stripped to the bare file at the archive root.
        handler.Add($"{ArchiveBase}/000104952126000030/primary_01.xml", HttpStatusCode.OK, PurchaseXml());
        // Already-bare primaryDocument stays as-is.
        handler.Add($"{ArchiveBase}/000104952126000029/form4.xml", HttpStatusCode.OK, SaleXml);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Items.Count); // only the two form=="4" rows

        // Assert the reader requested the STRIPPED raw XML URLs (and never the 8-K/10-Q docs).
        Assert.Contains($"{ArchiveBase}/000104952126000030/primary_01.xml", handler.RequestedUrls);
        Assert.Contains($"{ArchiveBase}/000104952126000029/form4.xml", handler.RequestedUrls);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("xslF345X06"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("mrcy-8k") || u.Contains("mrcy-10q"));
    }

    [Fact]
    public async Task ReadAsync_Purchase_YieldsPositiveWithNetValueSharesTimesPrice()
    {
        var filing = await ReadSingle("acc-p", "primary_01.xml", PurchaseXml());

        Assert.Equal(SignalDirection.Positive, filing.Direction);
        Assert.Equal(50_000m, filing.NetValue); // 1000 * 50
        Assert.Equal("JANE DOE", filing.PrimaryOwnerName);
        Assert.Equal("MRCY", filing.IssuerTicker);
        Assert.False(filing.Is10b5Plan);
        Assert.False(filing.HasCluster);
    }

    [Fact]
    public async Task ReadAsync_Sale_NotUnderPlan_YieldsNegativeWithSummedNetValue()
    {
        var filing = await ReadSingle("acc-s", "form4.xml", SaleXml);

        Assert.Equal(SignalDirection.Negative, filing.Direction);
        Assert.Equal(915_750m, filing.NetValue); // (8000 + 1250) * 99
        Assert.False(filing.Is10b5Plan);
    }

    [Fact]
    public async Task ReadAsync_Sale_UnderPlanFlag_YieldsNeutralZeroNetValue()
    {
        var filing = await ReadSingle("acc-plan", "form4.xml", PlannedSaleXml);

        Assert.Equal(SignalDirection.Neutral, filing.Direction);
        Assert.Equal(0m, filing.NetValue);
        Assert.True(filing.Is10b5Plan);
    }

    [Fact]
    public async Task ReadAsync_Sale_UnderFootnotePlan_YieldsNeutral()
    {
        var filing = await ReadSingle("acc-fn", "form4.xml", FootnotePlanSaleXml);

        Assert.Equal(SignalDirection.Neutral, filing.Direction);
        Assert.True(filing.Is10b5Plan);
    }

    [Fact]
    public async Task ReadAsync_Grant_YieldsNeutralExcluded()
    {
        var filing = await ReadSingle("acc-a", "form4.xml", GrantXml);

        Assert.Equal(SignalDirection.Neutral, filing.Direction);
        Assert.Equal(0m, filing.NetValue);
    }

    [Theory]
    [InlineData("M")]
    [InlineData("F")]
    [InlineData("G")]
    public async Task ReadAsync_NonDirectionalCodes_YieldNeutral(string code)
    {
        var filing = await ReadSingle($"acc-{code}", "form4.xml", SingleCodeXml(code));

        Assert.Equal(SignalDirection.Neutral, filing.Direction);
        Assert.Equal(0m, filing.NetValue);
    }

    [Fact]
    public async Task ReadAsync_MixedBuyAndSell_YieldsNeutralWithMaxNetValue()
    {
        var filing = await ReadSingle("acc-mix", "form4.xml", MixedBuySellXml);

        Assert.Equal(SignalDirection.Neutral, filing.Direction);
        Assert.Equal(5000m, filing.NetValue); // max(100*10=1000, 500*10=5000)
    }

    [Fact]
    public async Task ReadAsync_MultiOwnerSameDirection_SetsCluster()
    {
        var filing = await ReadSingle("acc-cluster", "form4.xml", ClusterPurchaseXml);

        Assert.Equal(SignalDirection.Positive, filing.Direction);
        Assert.Equal(2, filing.DistinctOwnerCount);
        Assert.True(filing.HasCluster);
    }

    [Fact]
    public async Task ReadAsync_HoldingOnly_IsIgnored_YieldsNeutralZero()
    {
        var filing = await ReadSingle("acc-hold", "form4.xml", HoldingOnlyXml);

        Assert.Equal(SignalDirection.Neutral, filing.Direction);
        Assert.Equal(0m, filing.NetValue);
    }

    [Fact]
    public async Task ReadAsync_MalformedSubmissions_ReturnsMalformed()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, "not { json");

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_EmptySubmissions_ReturnsMalformed()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, string.Empty);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_MissingCik_ReturnsMalformed()
    {
        // Valid JSON, valid filings.recent, but no 'cik' — proceeding would derive archive URLs from CIK "0"
        // and report a false zero-item Success, so this is a typed Malformed failure instead.
        const string noCik = """
            {
              "name": "MERCURY SYSTEMS INC",
              "filings": { "recent": { "form": ["4"], "filingDate": ["2026-06-02"],
                "acceptanceDateTime": ["2026-06-02T20:00:00.000Z"],
                "accessionNumber": ["0001049521-26-000030"], "primaryDocument": ["form4.xml"] } }
            }
            """;
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, noCik);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
        // No archive fetch attempted once the submissions payload is judged malformed.
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("form4.xml"));
    }

    [Fact]
    public async Task ReadAsync_MissingFilingsRecent_ReturnsMalformed()
    {
        // An object root with a cik but no filings.recent shape is a changed/malformed payload, not a quiet issuer.
        const string noRecent = """{ "cik": "1049521", "name": "MERCURY SYSTEMS INC" }""";
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, noRecent);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_OneFilingXml404_SkipsThatFiling_OthersParse()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, MixedSubmissions);
        // First Form 4 XML 404s; the second still parses.
        handler.Add($"{ArchiveBase}/000104952126000030/primary_01.xml", HttpStatusCode.NotFound, "missing");
        handler.Add($"{ArchiveBase}/000104952126000029/form4.xml", HttpStatusCode.OK, SaleXml);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Success, result.Outcome);
        var filing = Assert.Single(result.Items);
        Assert.Equal("0001049521-26-000029", filing.Accession);
        Assert.Equal(SignalDirection.Negative, filing.Direction);
    }

    [Fact]
    public async Task ReadAsync_OneFilingMalformedXml_SkipsThatFiling_OthersParse()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, MixedSubmissions);
        handler.Add($"{ArchiveBase}/000104952126000030/primary_01.xml", HttpStatusCode.OK, "<ownershipDocument><broken>");
        handler.Add($"{ArchiveBase}/000104952126000029/form4.xml", HttpStatusCode.OK, PurchaseXml());

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Success, result.Outcome);
        var filing = Assert.Single(result.Items);
        Assert.Equal("0001049521-26-000029", filing.Accession);
    }

    [Fact]
    public async Task ReadAsync_Http403_ReturnsForbidden()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.Forbidden, "forbidden");

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Forbidden, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_SubmissionsTimeout_ReturnsTimeout()
    {
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Timeout, result.Outcome);
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

    [Fact]
    public async Task ReadAsync_NonXmlPrimaryDocument_SkipsFilingWithoutFetch()
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, SingleForm4Submissions("acc-htm", "primary.htm"));

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecForm4ReadOutcome.Success, result.Outcome);
        Assert.Empty(result.Items);
        // No archive fetch attempted for a non-.xml primary document.
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("primary.htm"));
    }

    private static async Task<SecForm4Filing> ReadSingle(string accession, string primaryDocument, string xml)
    {
        var handler = new RoutingHandler();
        handler.Add(SubmissionsUrl, HttpStatusCode.OK, SingleForm4Submissions(accession, primaryDocument));
        var accNoDashes = accession.Replace("-", string.Empty);
        handler.Add($"{ArchiveBase}/{accNoDashes}/{primaryDocument}", HttpStatusCode.OK, xml);

        var result = await CreateReader(handler).ReadAsync(SubmissionsUrl, CancellationToken.None);
        Assert.Equal(SecForm4ReadOutcome.Success, result.Outcome);
        return Assert.Single(result.Items);
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
