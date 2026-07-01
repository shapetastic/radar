using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.UsaSpending;

namespace Radar.Infrastructure.Tests.UsaSpending;

public sealed class HttpUsaSpendingAwardReaderTests
{
    // A well-formed spending_by_award response: two contract awards for Mercury Systems, plus a benign
    // messages[] note that does NOT contain the "were not used" firehose warning.
    private const string ValidResults = """
        {
          "spending_level": "awards",
          "limit": 25,
          "results": [
            {
              "internal_id": 359630135,
              "Award ID": "N6893626P5106",
              "Recipient Name": "MERCURY SYSTEMS INC",
              "Award Amount": 159160.0,
              "Awarding Agency": "Department of Defense",
              "Start Date": "2026-03-24",
              "End Date": "2027-03-23",
              "Description": "PROCESSOR CARDS P/N# 910-56141-18",
              "recipient_id": "af09eaba-71de-97b6-660d-1adac9349c4d-C",
              "generated_internal_id": "CONT_AWD_N6893626P5106_9700_-NONE-_-NONE-"
            },
            {
              "internal_id": 359630200,
              "Award ID": "N6893626P5200",
              "Recipient Name": "MERCURY SYSTEMS INC",
              "Award Amount": 88000,
              "Awarding Agency": "Department of the Navy",
              "Start Date": "2026-01-10",
              "End Date": null,
              "Description": null,
              "recipient_id": "af09eaba-71de-97b6-660d-1adac9349c4d-C",
              "generated_internal_id": "CONT_AWD_N6893626P5200_9700_-NONE-_-NONE-"
            }
          ],
          "page_metadata": { "page": 1, "hasNext": true },
          "messages": [ "For searches, time period start date is usually 2007-10-01 or later." ]
        }
        """;

    // A firehose response: an unsupported filter key was silently ignored, flagged in messages[].
    private const string IgnoredFiltersResults = """
        {
          "spending_level": "awards",
          "limit": 25,
          "results": [
            {
              "internal_id": 1,
              "Award ID": "BIG-HUMANA",
              "Recipient Name": "HUMANA INC",
              "Award Amount": 51000000000.0,
              "Awarding Agency": "Department of Health and Human Services",
              "Start Date": "2025-01-01",
              "recipient_id": "some-other-recipient-id-C",
              "generated_internal_id": "CONT_AWD_HUMANA"
            }
          ],
          "page_metadata": { "page": 1, "hasNext": true },
          "messages": [ "The following filters from the request were not used: {'recipient_id'}." ]
        }
        """;

    private const string EmptyResults = """
        { "spending_level": "awards", "limit": 25, "results": [], "messages": [] }
        """;

    private static readonly UsaSpendingAwardQuery Query = new(
        SearchText: "Mercury Systems",
        StartDate: "2025-07-01",
        EndDate: "2026-07-01",
        AwardTypeCodes: ["A", "B", "C", "D"],
        Limit: 25);

    private static HttpUsaSpendingAwardReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<HttpUsaSpendingAwardReader>.Instance);

    [Fact]
    public async Task ReadAsync_ValidResults_ParsesAwards()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Items.Count);

        var first = result.Items[0];
        Assert.Equal("N6893626P5106", first.AwardId);
        Assert.Equal("MERCURY SYSTEMS INC", first.RecipientName);
        Assert.Equal(159160.0m, first.AwardAmount);
        Assert.Equal("Department of Defense", first.AwardingAgency);
        Assert.Equal("2026-03-24", first.StartDate);
        Assert.Equal("2027-03-23", first.EndDate);
        Assert.Equal("PROCESSOR CARDS P/N# 910-56141-18", first.Description);
        Assert.Equal("af09eaba-71de-97b6-660d-1adac9349c4d-C", first.RecipientId);
        Assert.Equal("CONT_AWD_N6893626P5106_9700_-NONE-_-NONE-", first.GeneratedInternalId);
        Assert.Equal(
            "https://www.usaspending.gov/award/CONT_AWD_N6893626P5106_9700_-NONE-_-NONE-",
            first.AwardUrl);

        // A JSON integer amount and null End Date/Description parse defensively.
        var second = result.Items[1];
        Assert.Equal(88000m, second.AwardAmount);
        Assert.Null(second.EndDate);
        Assert.Null(second.Description);
    }

    [Fact]
    public async Task ReadAsync_IgnoredFiltersWarning_ReturnsFiltersIgnoredWithZeroItems()
    {
        // The firehose guard: any "were not used" messages[] warning must yield NO awards.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, IgnoredFiltersResults));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.FiltersIgnored, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_Http400_ReturnsHttpErrorWithoutThrowing()
    {
        // An award-type-group validation error returns HTTP 400.
        var reader = CreateReader(new StubHandler(HttpStatusCode.BadRequest, "bad request"));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.HttpError, result.Outcome);
        Assert.Contains("400", result.Detail);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not { json"));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_EmptyBody_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, string.Empty));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.Malformed, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_UnexpectedRootShape_ReturnsMalformedWithoutThrowing(string body)
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_RecipientWithNoAwards_ReturnsSuccessWithNoItems()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyResults));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.Unreachable, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(Query, CancellationToken.None);

        Assert.Equal(UsaSpendingReadOutcome.Timeout, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidResults));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(Query, cts.Token));
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
