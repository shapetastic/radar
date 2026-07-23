using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Fcc;

namespace Radar.Infrastructure.Tests.Fcc;

public sealed class HttpFccAuthReaderTests
{
    // A well-formed EAS GenericSearch CSV: two authorizations for the grantee. The FCC ID is Grantee Code +
    // Product Code; the applicant name and one equipment class carry embedded commas inside quoted fields.
    private const string ValidCsv = """
        Grantee Code,Product Code,Grant Date,Applicant Name,Equipment Class
        ABC,123XYZ,05/12/2026,"Mercury Systems, Inc.","Digital Transmission System, Part 15"
        DEF,456,03/01/2026,"Mercury Systems, Inc.",Receiver
        """;

    // Same columns in a DIFFERENT order — parsing is BY HEADER NAME, so it must still work.
    private const string ReorderedColumnsCsv = """
        Equipment Class,Grant Date,Applicant Name,Product Code,Grantee Code
        Transmitter,05/12/2026,"Mercury Systems, Inc.",123XYZ,ABC
        """;

    private const string HeaderOnlyCsv = "Grantee Code,Product Code,Grant Date,Applicant Name,Equipment Class";

    // One valid row, one with an unparseable date, one with an absent (empty) date — only the valid row counts.
    private const string UnparseableGrantDatesCsv = """
        Grantee Code,Product Code,Grant Date,Applicant Name,Equipment Class
        ABC,111,05/12/2026,Valid Co,Transmitter
        DEF,222,not-a-date,Bad Date Co,Receiver
        GHI,333,,Absent Date Co,Module
        """;

    // A CSV whose header lacks the expected columns is a changed/bad export -> Malformed.
    private const string MissingColumnsCsv = """
        Foo,Bar,Baz
        1,2,3
        """;

    private static readonly DateOnly GrantFloor = new(2026, 1, 1);

    private static readonly DateTimeOffset FixedNow = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static HttpFccAuthReader CreateReader(
        HttpMessageHandler handler, FccCollectorOptions? options = null) =>
        new(
            new HttpClient(handler),
            NullLogger<HttpFccAuthReader>.Instance,
            options ?? new FccCollectorOptions(),
            new FixedTimeProvider(FixedNow));

    [Fact]
    public async Task ReadAsync_ValidCsv_ParsesCountFccIdsAndGrantDates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidCsv));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Result!.GrantCount);
        // Both rows fit under the default page cap, so the count is exact, not a floor.
        Assert.False(result.Result.Truncated);

        var first = result.Result.Grants[0];
        // FCC ID = Grantee Code + Product Code concatenated.
        Assert.Equal("ABC123XYZ", first.FccId);
        Assert.Equal("Digital Transmission System, Part 15", first.Description);
        Assert.Equal(new DateOnly(2026, 5, 12), first.GrantDate);

        var second = result.Result.Grants[1];
        Assert.Equal("DEF456", second.FccId);
        Assert.Equal(new DateOnly(2026, 3, 1), second.GrantDate);
    }

    [Fact]
    public async Task ReadAsync_ReorderedColumns_ParsedByHeaderName()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ReorderedColumnsCsv));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Success, result.Outcome);
        var grant = Assert.Single(result.Result!.Grants);
        Assert.Equal("ABC123XYZ", grant.FccId);
        Assert.Equal("Transmitter", grant.Description);
        Assert.Equal(new DateOnly(2026, 5, 12), grant.GrantDate);
    }

    [Fact]
    public async Task ReadAsync_HeaderOnly_ReturnsSuccessWithZeroGrants()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, HeaderOnlyCsv));

        var result = await reader.ReadAsync("Nobody, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.GrantCount);
        Assert.Empty(result.Result.Grants);
    }

    [Fact]
    public async Task ReadAsync_RowsWithUnparseableGrantDate_AreSkipped()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, UnparseableGrantDatesCsv));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Success, result.Outcome);
        // Only the single row with a valid grant date survives; the bad/absent dates are dropped, not coerced.
        var grant = Assert.Single(result.Result!.Grants);
        Assert.Equal(1, result.Result.GrantCount);
        Assert.Equal("ABC111", grant.FccId);
        Assert.Equal(new DateOnly(2026, 5, 12), grant.GrantDate);
    }

    [Fact]
    public async Task ReadAsync_HonoursMaxPageSize()
    {
        var reader = CreateReader(
            new StubHandler(HttpStatusCode.OK, ValidCsv), new FccCollectorOptions { MaxPageSize = 1 });

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Success, result.Outcome);
        Assert.Equal(1, result.Result!.GrantCount);
        // ValidCsv has a SECOND valid grant beyond the cap of 1, so the count is a floor, not an exact total.
        Assert.True(result.Result.Truncated);
    }

    [Fact]
    public async Task ReadAsync_ExactlyMaxPageSize_IsNotTruncated()
    {
        // Cap of 2 with exactly 2 valid grants: the cap is reached but nothing valid remains, so NOT truncated.
        var reader = CreateReader(
            new StubHandler(HttpStatusCode.OK, ValidCsv), new FccCollectorOptions { MaxPageSize = 2 });

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(2, result.Result!.GrantCount);
        Assert.False(result.Result.Truncated);
    }

    [Fact]
    public async Task ReadAsync_CapReachedButRemainingRowsInvalid_IsNotTruncated()
    {
        // One valid grant then only unparseable/absent-date rows: at a cap of 1 the leftover rows are NOT
        // valid grants, so truncation detection (valid-grants only) must report NOT truncated.
        var reader = CreateReader(
            new StubHandler(HttpStatusCode.OK, UnparseableGrantDatesCsv),
            new FccCollectorOptions { MaxPageSize = 1 });

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(1, result.Result!.GrantCount);
        Assert.False(result.Result.Truncated);
    }

    [Fact]
    public async Task ReadAsync_MissingExpectedColumns_ReturnsMalformed()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, MissingColumnsCsv));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Malformed, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ReadAsync_EmptyBody_ReturnsMalformed()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, string.Empty));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessStatus_ReturnsHttpError()
    {
        // A datacenter-IP Akamai 403 is the expected production failure mode; it maps to HttpError.
        var reader = CreateReader(new StubHandler(HttpStatusCode.Forbidden, "forbidden"));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.HttpError, result.Outcome);
        Assert.Contains("403", result.Detail);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeout()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachable()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, CancellationToken.None);

        Assert.Equal(FccAuthOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidCsv));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync("Mercury Systems, Inc.", GrantFloor, cts.Token));
    }

    [Fact]
    public void QueryUrl_EncodesGranteeAndGrantFloor()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidCsv));

        var url = reader.QueryUrl("Mercury Systems, Inc.", GrantFloor);

        Assert.StartsWith(
            "https://apps.fcc.gov/oetcf/eas/reports/GenericSearchResult.cfm?", url, StringComparison.Ordinal);
        Assert.Contains("applicant_name=", url, StringComparison.Ordinal);
        Assert.Contains("grant_date_from=", url, StringComparison.Ordinal);
        Assert.Contains("grant_date_to=", url, StringComparison.Ordinal);
        // The grantee name is URL-encoded, so a raw space never appears in the URL.
        Assert.DoesNotContain(' ', url);
        // The grant floor is emitted MM/dd/yyyy (US-style, as EAS expects).
        Assert.Contains("01/01/2026", Uri.UnescapeDataString(url), StringComparison.Ordinal);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/csv"),
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
