using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Fda;

namespace Radar.Infrastructure.Tests.Fda;

public sealed class HttpFdaClearanceReaderTests
{
    // A well-formed openFDA 510(k) response: two clearances plus the meta envelope.
    private const string Valid510k = """
        {
          "meta": { "results": { "total": 12 } },
          "results": [
            { "k_number": "K250001", "device_name": "Nerve repair conduit", "decision_date": "2026-05-12", "applicant": "Axogen" },
            { "k_number": "K250002", "device_name": "Surgical implant scaffold", "decision_date": "2026-03-01", "applicant": "Axogen" }
          ]
        }
        """;

    // A well-formed openFDA PMA response: PMA uses pma_number + trade_name (its device_name is null/absent).
    private const string ValidPma = """
        {
          "meta": { "results": { "total": 3 } },
          "results": [
            { "pma_number": "P250010", "trade_name": "Organ perfusion module", "decision_date": "2026-04-20", "applicant": "TransMedics" }
          ]
        }
        """;

    // Rows carrying an unparseable/absent decision_date must be skipped, not coerced. Only the valid row counts.
    private const string UnparseableDates510k = """
        {
          "meta": { "results": { "total": 3 } },
          "results": [
            { "k_number": "K250001", "device_name": "Valid row", "decision_date": "2026-05-12" },
            { "k_number": "K250002", "device_name": "Bad date", "decision_date": "not-a-date" },
            { "k_number": "K250003", "device_name": "Absent date" }
          ]
        }
        """;

    // openFDA reports a genuinely empty search as HTTP 404 with this body (NOT an empty results array).
    private const string EmptySearch404 = """
        { "error": { "code": "NOT_FOUND", "message": "No matches found!" } }
        """;

    private const string NoResultsArray = """
        { "meta": { "results": { "total": 0 } } }
        """;

    private static readonly DateOnly DecisionFloor = new(2026, 1, 1);

    private static HttpFdaClearanceReader CreateReader(
        HttpMessageHandler handler, FdaCollectorOptions? options = null) =>
        new(
            new HttpClient(handler),
            NullLogger<HttpFdaClearanceReader>.Instance,
            options ?? new FdaCollectorOptions());

    [Fact]
    public async Task ReadAsync_ValidResults_MergesBothEndpointsWithCountsNamesDatesAndTracks()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, Valid510k),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Result);
        // Two 510(k) + one PMA merged.
        Assert.Equal(3, result.Result!.ClearanceCount);
        Assert.Equal(12, result.Result.ReportedTotal510k);
        Assert.Equal(3, result.Result.ReportedTotalPma);

        var first = result.Result.Clearances[0];
        Assert.Equal("K250001", first.SubmissionNumber);
        Assert.Equal("Nerve repair conduit", first.DeviceName);
        Assert.Equal(new DateOnly(2026, 5, 12), first.DecisionDate);
        Assert.Equal("510(k)", first.Track);

        // The PMA row: pma_number as the submission number, trade_name as the device name, PMA track.
        var pma = result.Result.Clearances[^1];
        Assert.Equal("P250010", pma.SubmissionNumber);
        Assert.Equal("Organ perfusion module", pma.DeviceName);
        Assert.Equal(new DateOnly(2026, 4, 20), pma.DecisionDate);
        Assert.Equal("PMA", pma.Track);
    }

    [Fact]
    public async Task ReadAsync_BothEndpointsEmptySearch404_ReturnsSuccessWithZeroClearances()
    {
        // openFDA's documented empty-search 404 is a valid no-recent-clearances result, not an error.
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.NotFound, EmptySearch404),
            pma: (HttpStatusCode.NotFound, EmptySearch404)));

        var result = await reader.ReadAsync("Nobody Devices", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        Assert.Equal(0, result.Result!.ClearanceCount);
        Assert.Empty(result.Result.Clearances);
        Assert.Equal(0, result.Result.ReportedTotal510k);
        Assert.Equal(0, result.Result.ReportedTotalPma);
    }

    [Fact]
    public async Task ReadAsync_OneEndpoint404_OtherHasResults_MergesTheNon404Endpoint()
    {
        // A 404 on 510(k) contributes 0; the PMA endpoint's clearances still come through.
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.NotFound, EmptySearch404),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("TransMedics", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        var clearance = Assert.Single(result.Result!.Clearances);
        Assert.Equal("P250010", clearance.SubmissionNumber);
        Assert.Equal(0, result.Result.ReportedTotal510k);
        Assert.Equal(3, result.Result.ReportedTotalPma);
    }

    [Fact]
    public async Task ReadAsync_RowsWithUnparseableDecisionDate_AreSkipped()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, UnparseableDates510k),
            pma: (HttpStatusCode.NotFound, EmptySearch404)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Success, result.Outcome);
        // Only the single row with a valid decision_date survives; the bad/absent dates are dropped, not coerced.
        var clearance = Assert.Single(result.Result!.Clearances);
        Assert.Equal(1, result.Result.ClearanceCount);
        Assert.Equal("K250001", clearance.SubmissionNumber);
        Assert.Equal(new DateOnly(2026, 5, 12), clearance.DecisionDate);
    }

    [Fact]
    public async Task ReadAsync_MissingResultsArray_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, NoResultsArray),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Malformed, result.Outcome);
        Assert.Null(result.Result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_UnexpectedRootShape_ReturnsMalformed(string body)
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, body),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformed()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, "this is not { json"),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessNon404Status_ReturnsHttpError()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.Forbidden, "forbidden"),
            pma: (HttpStatusCode.OK, ValidPma)));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.HttpError, result.Outcome);
        Assert.Contains("403", result.Detail);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeout()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachable()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync("Axogen", DecisionFloor, CancellationToken.None);

        Assert.Equal(FdaReadOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, Valid510k),
            pma: (HttpStatusCode.OK, ValidPma)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync("Axogen", DecisionFloor, cts.Token));
    }

    [Fact]
    public void QueryUrl_EncodesApplicantAndDecisionFloor_ReturnsThe510kEndpoint()
    {
        var reader = CreateReader(new RoutingHandler(
            k510: (HttpStatusCode.OK, Valid510k),
            pma: (HttpStatusCode.OK, ValidPma)));

        var url = reader.QueryUrl("TransMedics", DecisionFloor);

        Assert.StartsWith("https://api.fda.gov/device/510k.json?search=", url, StringComparison.Ordinal);
        Assert.Contains("&limit=", url, StringComparison.Ordinal);
        // The search expression is URL-encoded, so raw spaces never appear.
        Assert.DoesNotContain(' ', url);
        var decoded = Uri.UnescapeDataString(url);
        Assert.Contains("applicant:TransMedics", decoded, StringComparison.Ordinal);
        Assert.Contains("2026-01-01", decoded, StringComparison.Ordinal);
        Assert.Contains("9999-12-31", decoded, StringComparison.Ordinal);
    }

    // Routes to the 510(k) or PMA canned response by the request URL host path, so a single reader call that
    // hits both endpoints gets the right body for each.
    private sealed class RoutingHandler(
        (HttpStatusCode Status, string Body) k510, (HttpStatusCode Status, string Body) pma) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var (status, body) = url.Contains("/device/pma.json", StringComparison.Ordinal) ? pma : k510;
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
