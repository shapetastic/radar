using System.Net;
using System.Text;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// Direct tests of the shared fetch/outcome ladder every reader now routes through. The ORDER of the ladder is
/// the contract: the caller's status hook wins over the generic non-success branch, and genuine caller
/// cancellation re-throws while the request's own timeout maps to a typed failure — a regression in either
/// would silently change every collector's behavior at once, which is exactly why this primitive is shared.
/// </summary>
public sealed class HttpOutcomeFetchTests
{
    // A minimal reference failure type standing in for the readers' own *ReadResult records.
    private sealed record Failure(string Reason);

    private static Task<(Failure? Failure, string? Body)> FetchAsync(
        HttpMessageHandler handler,
        CancellationToken ct,
        Func<int, Failure?>? onStatus = null) =>
        HttpOutcomeFetch.GetAsync<Failure, string>(
            new HttpClient(handler),
            "https://example.test/resource",
            readBody: (content, c) => content.ReadAsStringAsync(c),
            onStatus,
            onHttpError: status => new Failure($"http:{status}"),
            onUnreachable: _ => new Failure("unreachable"),
            onTimeout: _ => new Failure("timeout"),
            ct);

    [Fact]
    public async Task GetAsync_Success_ReturnsBodyAndNoFailure()
    {
        var (failure, body) = await FetchAsync(
            new StubHandler(HttpStatusCode.OK, "payload"), CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal("payload", body);
    }

    [Fact]
    public async Task GetAsync_NonSuccess_MapsThroughOnHttpErrorWithStatusCode()
    {
        var (failure, body) = await FetchAsync(
            new StubHandler(HttpStatusCode.InternalServerError, "boom"), CancellationToken.None);

        Assert.Equal(new Failure("http:500"), failure);
        Assert.Null(body);
    }

    [Fact]
    public async Task GetAsync_StatusHook_FiresBeforeTheGenericNonSuccessBranch()
    {
        // The hook must win for the status it claims (429/403), never falling through to onHttpError.
        var (failure, body) = await FetchAsync(
            new StubHandler(HttpStatusCode.TooManyRequests, "slow down"),
            CancellationToken.None,
            onStatus: status => status == 429 ? new Failure("rate-limited") : null);

        Assert.Equal(new Failure("rate-limited"), failure);
        Assert.Null(body);
    }

    [Fact]
    public async Task GetAsync_StatusHookReturningNull_FallsThroughToOnHttpError()
    {
        var (failure, _) = await FetchAsync(
            new StubHandler(HttpStatusCode.NotFound, "gone"),
            CancellationToken.None,
            onStatus: status => status == 429 ? new Failure("rate-limited") : null);

        Assert.Equal(new Failure("http:404"), failure);
    }

    [Fact]
    public async Task GetAsync_StatusHookOnSuccessStatus_StillWins()
    {
        // The hook is consulted for EVERY status, including 2xx — a caller that claims one short-circuits
        // before the body is ever read.
        var (failure, body) = await FetchAsync(
            new StubHandler(HttpStatusCode.OK, "payload"),
            CancellationToken.None,
            onStatus: status => status == 200 ? new Failure("claimed") : null);

        Assert.Equal(new Failure("claimed"), failure);
        Assert.Null(body);
    }

    [Fact]
    public async Task GetAsync_HttpRequestException_MapsThroughOnUnreachable()
    {
        var (failure, body) = await FetchAsync(
            new ThrowingHandler(new HttpRequestException("network down")), CancellationToken.None);

        Assert.Equal(new Failure("unreachable"), failure);
        Assert.Null(body);
    }

    [Fact]
    public async Task GetAsync_RequestTimeout_MapsThroughOnTimeout()
    {
        // A TaskCanceledException with the caller's token NOT cancelled is the request's own deadline.
        var (failure, body) = await FetchAsync(
            new ThrowingHandler(new TaskCanceledException("timed out")), CancellationToken.None);

        Assert.Equal(new Failure("timeout"), failure);
        Assert.Null(body);
    }

    [Fact]
    public async Task GetAsync_CallerCancellation_Throws_AndIsNeverMappedToAFailure()
    {
        // The `when (ct.IsCancellationRequested)` catch MUST sit ahead of the TaskCanceledException catch:
        // genuine caller cancellation stops the run, it does not degrade to a typed failure. (The stub honours
        // the token the way a real transport does — a cancelled send surfaces as a TaskCanceledException.)
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FetchAsync(new CancellationHonouringHandler(HttpStatusCode.OK, "payload"), cts.Token));
    }

    [Fact]
    public async Task GetAsync_CancellationRaisedMidRequest_Throws_AndIsNeverMappedToATimeout()
    {
        // The same TaskCanceledException shape as a timeout, but with the caller's token cancelled while the
        // request is in flight: the filtered catch must win, so it re-throws instead of mapping to onTimeout.
        using var cts = new CancellationTokenSource();
        var handler = new ThrowingHandler(new TaskCanceledException("cancelled"), cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FetchAsync(handler, cts.Token));
    }

    [Fact]
    public async Task SendAsync_UsesTheCallerSuppliedRequest()
    {
        // The core entry point lets a caller own the verb (USASpending POSTs); the ladder is unchanged.
        var handler = new RecordingHandler(HttpStatusCode.OK, "posted");
        var httpClient = new HttpClient(handler);

        var (failure, body) = await HttpOutcomeFetch.SendAsync<Failure, string>(
            send: c => httpClient.PostAsync("https://example.test/search", new StringContent("{}"), c),
            readBody: (content, c) => content.ReadAsStringAsync(c),
            onStatus: null,
            onHttpError: status => new Failure($"http:{status}"),
            onUnreachable: _ => new Failure("unreachable"),
            onTimeout: _ => new Failure("timeout"),
            CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal("posted", body);
        Assert.Equal(HttpMethod.Post, handler.Method);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Like <see cref="StubHandler"/> but observing the request's cancellation token, as a real transport does:
    /// a cancelled send throws <see cref="TaskCanceledException"/> from inside the ladder's try.
    /// </summary>
    private sealed class CancellationHonouringHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Throws the supplied exception from the transport. When a <see cref="CancellationTokenSource"/> is
    /// supplied it is cancelled first, so the throw arrives with the caller's token already cancelled (the
    /// genuine-cancellation path) rather than as an HTTP timeout.
    /// </summary>
    private sealed class ThrowingHandler(Exception exception, CancellationTokenSource? cancelFirst = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancelFirst?.Cancel();
            throw exception;
        }
    }
}
