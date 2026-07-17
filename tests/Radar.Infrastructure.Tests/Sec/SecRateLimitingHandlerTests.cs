using System.Net;

using Microsoft.Extensions.Time.Testing;

using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

/// <summary>
/// Covers <see cref="SecRateLimitingHandler"/>: it routes every outbound SEC request through the shared
/// <see cref="SecRequestPacer"/> BEFORE sending it (so the aggregate *.sec.gov rate is bounded at the
/// HttpClient message-handler level), then applies a per-fetch timeout started only AFTER pacing (so a deep
/// pacer queue can never time a request out before it is sent). A <see cref="FakeTimeProvider"/> makes both the
/// pacing and the fetch timeout deterministic and offline.
/// </summary>
public sealed class SecRateLimitingHandlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    private static SecRateLimitingHandler CreateHandler(
        TimeSpan minInterval, TimeSpan fetchTimeout, TimeProvider timeProvider, HttpMessageHandler inner) =>
        new(
            new SecRequestPacer(new SecRateLimitOptions { MinInterval = minInterval }, timeProvider),
            new SecRateLimitOptions { MinInterval = minInterval, FetchTimeout = fetchTimeout },
            timeProvider)
        {
            InnerHandler = inner,
        };

    [Fact]
    public async Task SendAsync_PacesSuccessiveRequestsThroughSharedPacer()
    {
        var fake = new FakeTimeProvider(Start);
        var inner = new CountingHandler();
        var handler = CreateHandler(TimeSpan.FromMilliseconds(150), TimeSpan.Zero, fake, inner);
        var invoker = new HttpMessageInvoker(handler);

        // First request goes out immediately (no prior SEC request).
        var first = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://data.sec.gov/a"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, inner.Count);

        // Second request must await the pacing interval: it has NOT reached the inner handler until the clock
        // advances by MinInterval.
        var secondTask = invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://www.sec.gov/b"), CancellationToken.None);
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(1, inner.Count);

        fake.Advance(TimeSpan.FromMilliseconds(150));

        var second = await secondTask;
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public async Task SendAsync_FetchTimeoutStartsAfterPacing_AndCancelsAHangingFetch()
    {
        var fake = new FakeTimeProvider(Start);
        var inner = new HangingHandler();
        // No pacing delay so the first request reaches the fetch immediately; the fetch then hangs and must be
        // cancelled by the handler-owned 30s budget once the clock advances past it.
        var handler = CreateHandler(TimeSpan.Zero, TimeSpan.FromSeconds(30), fake, inner);
        var invoker = new HttpMessageInvoker(handler);

        var task = invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://www.sec.gov/slow"), CancellationToken.None);

        // The fetch is in flight (hanging) and its budget has not elapsed yet.
        Assert.False(task.IsCompleted);
        fake.Advance(TimeSpan.FromSeconds(29));
        Assert.False(task.IsCompleted);

        // Crossing the 30s budget cancels the fetch. The caller's token was NOT cancelled, so this surfaces as a
        // TaskCanceledException (which HttpOutcomeFetch maps to the readers' typed timeout outcome), not caller
        // cancellation.
        fake.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TaskCanceledException>(() => task);
    }

    [Fact]
    public async Task SendAsync_CallerCancellation_Propagates()
    {
        var fake = new FakeTimeProvider(Start);
        var inner = new HangingHandler();
        var handler = CreateHandler(TimeSpan.Zero, TimeSpan.FromSeconds(30), fake, inner);
        var invoker = new HttpMessageInvoker(handler);
        using var cts = new CancellationTokenSource();

        var task = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://www.sec.gov/slow"), cts.Token);
        Assert.False(task.IsCompleted);

        // Genuine caller cancellation must propagate (it is linked into the fetch's token).
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    // Never completes on its own — only the passed CancellationToken ends the wait (mimicking a stalled fetch).
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
