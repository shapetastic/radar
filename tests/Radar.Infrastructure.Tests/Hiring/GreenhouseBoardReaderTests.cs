using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.Hiring;

namespace Radar.Infrastructure.Tests.Hiring;

public sealed class GreenhouseBoardReaderTests
{
    private const string BoardToken = "mercury";

    // A well-formed Greenhouse board response: three role entries, one with a blank title (a parsed role
    // whose title contributes nothing), plus a meta.total that is deliberately WRONG — the reader must
    // count parsed entries, never trust meta.total.
    private const string ValidJobs = """
        {
          "jobs": [
            { "id": 1, "title": "Senior Software Engineer" },
            { "id": 2, "title": "   " },
            { "id": 3, "title": "VP, Strategic Partnerships" }
          ],
          "meta": { "total": 999 }
        }
        """;

    private const string EmptyJobs = """
        { "jobs": [] }
        """;

    private static GreenhouseBoardReader CreateReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<GreenhouseBoardReader>.Instance);

    [Fact]
    public void Platform_IsGreenhouse()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyJobs));

        Assert.Equal("greenhouse", reader.Platform);
    }

    [Fact]
    public void BoardUrl_IsTheResolvedBoardApiUrl()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyJobs));

        Assert.Equal(
            "https://boards-api.greenhouse.io/v1/boards/mercury/jobs",
            reader.BoardUrl(BoardToken));
    }

    [Fact]
    public async Task ReadAsync_ValidJobs_CountsParsedEntriesAndSkipsBlankTitles()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ValidJobs);
        var reader = CreateReader(handler);

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Result);

        // TotalRoles = the count of parsed job entries (3), NEVER meta.total (999).
        Assert.Equal(3, result.Result!.TotalRoles);

        // The blank title is skipped from the title list only.
        Assert.Equal(
            ["Senior Software Engineer", "VP, Strategic Partnerships"],
            result.Result.Titles);

        // The GET hit the resolved board API URL.
        Assert.Equal(reader.BoardUrl(BoardToken), handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ReadAsync_EmptyJobsArray_ReturnsSuccessWithZeroRoles()
    {
        // A board with no openings is a valid Success (zero roles), not an error.
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, EmptyJobs));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Success, result.Outcome);
        Assert.NotNull(result.Result);
        Assert.Equal(0, result.Result!.TotalRoles);
        Assert.Empty(result.Result.Titles);
    }

    [Fact]
    public async Task ReadAsync_MissingJobsArray_ReturnsMalformedWithoutThrowing()
    {
        // A valid object WITHOUT the jobs array is a bad/changed payload, not a no-openings board (an
        // empty board still returns "jobs": []).
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, """{ "meta": { "total": 0 } }"""));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Malformed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public async Task ReadAsync_UnexpectedRootShape_ReturnsMalformedWithoutThrowing(string body)
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(BoardToken, CancellationToken.None);

        Assert.Equal(JobBoardReadOutcome.Malformed, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ReadAsync_MalformedJson_ReturnsMalformedWithoutThrowing()
    {
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, "this is not { json"));

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
        var reader = CreateReader(new StubHandler(HttpStatusCode.OK, ValidJobs));
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
