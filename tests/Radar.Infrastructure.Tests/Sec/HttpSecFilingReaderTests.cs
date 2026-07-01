using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class HttpSecFilingReaderTests
{
    // Columnar submissions fixture: three filings, newest-first. One 8-K (with item codes), one 10-Q, one
    // 10-K, plus a stray Form 4 the collector's filter would later drop (the reader still parses all rows).
    private const string ValidSubmissions = """
        {
          "cik": "1049521",
          "name": "MERCURY SYSTEMS INC",
          "tickers": ["MRCY"],
          "exchanges": ["NASDAQ"],
          "filings": {
            "recent": {
              "form": ["8-K", "10-Q", "10-K", "4"],
              "filingDate": ["2026-06-02", "2026-05-01", "2026-02-15", "2026-06-01"],
              "reportDate": ["2026-06-01", "2026-03-31", "2025-12-31", ""],
              "acceptanceDateTime": ["2026-06-02T16:30:00.000Z", "2026-05-01T17:05:00.000Z", "2026-02-15T06:01:00.000Z", "2026-06-01T20:00:00.000Z"],
              "accessionNumber": ["0001049521-26-000011", "0001049521-26-000009", "0001049521-26-000004", "0001049521-26-000010"],
              "primaryDocument": ["mrcy-8k.htm", "mrcy-10q.htm", "mrcy-10k.htm", "form4.xml"],
              "primaryDocDescription": ["Current report", "Quarterly report", "Annual report", "Statement of changes"],
              "items": ["2.02,9.01", "", "", ""]
            }
          }
        }
        """;

    private const string EmptySubmissions = """
        {
          "cik": "0000000",
          "name": "DELISTED CO",
          "tickers": [],
          "exchanges": [],
          "filings": {
            "recent": {
              "form": [],
              "filingDate": [],
              "acceptanceDateTime": [],
              "accessionNumber": []
            }
          }
        }
        """;

    private static HttpSecFilingReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<HttpSecFilingReader>.Instance);

    private const string SubmissionsUrl = "https://data.sec.gov/submissions/CIK0001049521.json";

    [Fact]
    public async Task ReadAsync_ValidSubmissions_ParsesColumnarArrays()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidSubmissions));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Items.Count);

        var eightK = result.Items[0];
        Assert.Equal("8-K", eightK.Form);
        Assert.Equal("2026-06-02", eightK.FilingDate);
        Assert.Equal("0001049521-26-000011", eightK.Accession);
        Assert.Equal("2.02,9.01", eightK.Items);
        Assert.Equal("Current report", eightK.PrimaryDocDescription);
        Assert.Equal("mrcy-8k.htm", eightK.PrimaryDocument);

        // acceptanceDateTime is parsed to a UTC instant (the observed/published moment).
        Assert.Equal(
            new DateTimeOffset(2026, 6, 2, 16, 30, 0, TimeSpan.Zero),
            eightK.AcceptanceDateTimeUtc);
        Assert.Equal(TimeSpan.Zero, eightK.AcceptanceDateTimeUtc.Offset);

        // Index URL: CIK leading zeros stripped, accession dashes stripped in the path but kept in the filename.
        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/1049521/000104952126000011/0001049521-26-000011-index.htm",
            eightK.IndexUrl);
    }

    [Fact]
    public async Task ReadAsync_NonEightKForm_HasNoItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidSubmissions));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        var tenQ = result.Items.Single(i => i.Form == "10-Q");
        Assert.Null(tenQ.Items);
    }

    [Fact]
    public async Task ReadAsync_Http403_ReturnsForbiddenWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.Forbidden, "forbidden"));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.Forbidden, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessStatus_ReturnsHttpErrorWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.NotFound, "missing"));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.HttpError, result.Outcome);
        Assert.Contains("404", result.Detail);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not { json"));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_EmptyBody_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, string.Empty));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_QuietButValidIssuer_ReturnsSuccessWithNoItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptySubmissions));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.Unreachable, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(SubmissionsUrl, CancellationToken.None);

        Assert.Equal(SecFilingReadOutcome.Timeout, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidSubmissions));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(SubmissionsUrl, cts.Token));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
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
