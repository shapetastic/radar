using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Hiring;

namespace Radar.Infrastructure.Tests.Hiring;

public sealed class LeverBoardReaderTests
{
    private const string BoardToken = "energyrecovery";

    // A well-formed Lever postings response: a top-level ARRAY of postings, one missing its "text" (a
    // parsed role whose title contributes nothing).
    private const string ValidPostings = """
        [
          { "id": "a", "text": "Principal Research Scientist" },
          { "id": "b" },
          { "id": "c", "text": "Field Service Technician" }
        ]
        """;

    private const string EmptyPostings = "[]";

    private static LeverBoardReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<LeverBoardReader>.Instance);

    [Fact]
    public void Platform_IsLever()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyPostings));

        Assert.Equal("lever", reader.Platform);
    }

    [Fact]
    public void BoardUrl_IsTheResolvedBoardApiUrl()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyPostings));

        Assert.Equal(
            "https://api.lever.co/v0/postings/energyrecovery?mode=json",
            reader.BoardUrl(BoardToken));
    }

    [Fact]
    public async Task ReadAsync_ValidPostings_CountsParsedEntriesAndSkipsMissingText()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ValidPostings);
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Result);

        // TotalRoles = the count of parsed posting entries (3); the text-less posting still counts.
        Assert.Equal(3, result.Result!.TotalRoles);
        Assert.Equal(
            ["Principal Research Scientist", "Field Service Technician"],
            result.Result.Titles);

        // The GET hit the resolved board API URL (mode=json).
        Assert.Equal(reader.BoardUrl(BoardToken), handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ReadAsync_EmptyArray_ReturnsSuccessWithZeroRoles()
    {
        // A board with no openings is a valid Success (zero roles), not an error.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyPostings));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Success, result.Outcome);
        Assert.NotNull(result.Result);
        Assert.Equal(0, result.Result!.TotalRoles);
        Assert.Empty(result.Result.Titles);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "postings": [] }""")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_RootNotArray_ReturnsMalformedWithoutThrowing(string body)
    {
        // The postings endpoint (mode=json) returns a top-level array; any other root is a bad/changed
        // payload, never a silent zero-role success.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not [ json"));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Malformed, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ReadAsync_NonSuccessStatus_ReturnsHttpErrorWithoutThrowing()
    {
        // A bad board token 404s — the typed HttpError failure, never a throw.
        var reader = CreateReader(new StubHandler(HttpStatusCode.NotFound, "not found"));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.HttpError, result.Outcome);
        Assert.Contains("404", result.Detail);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ReadAsync_HttpRequestException_ReturnsUnreachableWithoutThrowing()
    {
        var reader = CreateReader(new ThrowingHandler(new HttpRequestException("network down")));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Unreachable, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ReadAsync_RequestTimeout_ReturnsTimeoutWithoutThrowing()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var reader = CreateReader(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Timeout, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidPostings));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(BoardToken, cts.Token));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
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
