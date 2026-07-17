namespace Radar.Infrastructure.Sec;

/// <summary>
/// <see cref="DelegatingHandler"/> that routes every SEC <c>HttpClient</c>'s outbound request through the
/// shared singleton <see cref="SecRequestPacer"/> before sending it, so the AGGREGATE SEC request rate
/// across all collectors and the earnings-release reader stays under SEC's per-IP fair-access ceiling.
/// Registered transient (the <c>HttpClientFactory</c> owns handler lifetime); the pacing STATE lives in the
/// injected singleton pacer, which every SEC client's handler instance shares — so it is the whole run's
/// SEC traffic that is bounded, not each client on its own.
/// <para>
/// The handler ALSO owns the per-fetch timeout (<see cref="SecRateLimitOptions.FetchTimeout"/>), started only
/// AFTER the pacer grants the turn. The SEC clients therefore disable their ambient
/// <see cref="HttpClient.Timeout"/> (set it to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) and let
/// this handler time the fetch instead: with the ambient timeout the pacing wait and the fetch shared one
/// budget, so a deep pacer queue (the watch universe scaling up) could cancel a request before it was ever
/// sent. Waiting on the pacer is bounded only by the caller's token; the fetch gets its own fresh budget. When
/// only that fetch budget elapses the caller's token is NOT cancelled, so the resulting
/// <see cref="TaskCanceledException"/> maps to the readers' typed timeout outcome (via <c>HttpOutcomeFetch</c>)
/// exactly as an ambient <see cref="HttpClient.Timeout"/> did; genuine caller cancellation still propagates.
/// </para>
/// </summary>
internal sealed class SecRateLimitingHandler : DelegatingHandler
{
    private readonly SecRequestPacer _pacer;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _fetchTimeout;

    public SecRateLimitingHandler(SecRequestPacer pacer, SecRateLimitOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(pacer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (options.FetchTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"SEC {nameof(SecRateLimitOptions.FetchTimeout)} must not be negative; was {options.FetchTimeout}. "
                    + "A negative fetch timeout is nonsensical configuration.");
        }

        _pacer = pacer;
        _timeProvider = timeProvider;
        _fetchTimeout = options.FetchTimeout;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Wait our turn in the shared pacer FIRST, bounded only by caller cancellation — never by the fetch
        // timeout, so pacing delay can never consume the fetch's round-trip budget however deep the queue.
        await _pacer.WaitTurnAsync(cancellationToken).ConfigureAwait(false);

        // Zero ⇒ handler-owned timeout disabled; the fetch is bounded only by the caller's token.
        if (_fetchTimeout <= TimeSpan.Zero)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Give the actual fetch its OWN budget, started only now (after pacing). The timeout runs off the injected
        // TimeProvider (deterministic under a fake clock in tests). Link with the caller's token so genuine caller
        // cancellation still propagates; when only the fetch timeout fires the caller's token stays un-cancelled,
        // so HttpOutcomeFetch maps the resulting TaskCanceledException to the readers' typed timeout outcome.
        using var timeoutCts = new CancellationTokenSource(_fetchTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        return await base.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
    }
}
