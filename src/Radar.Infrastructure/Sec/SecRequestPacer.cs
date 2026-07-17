namespace Radar.Infrastructure.Sec;

/// <summary>
/// Process-wide pacer that serializes and rate-limits ALL outbound SEC (<c>*.sec.gov</c>) HTTP requests so
/// a whole run stays under SEC's per-IP fair-access ceiling. Registered as a singleton and shared by every
/// <see cref="SecRateLimitingHandler"/> instance across every SEC <c>HttpClient</c> (the 3 collectors'
/// readers + the earnings-release reader), so it is the AGGREGATE rate — not each client independently —
/// that is bounded. This is the fix for the observed failure mode where an unpaced collector burst
/// (~3 SEC collectors × N companies of <c>data.sec.gov</c> requests) trips SEC's mitigation and blocks the
/// stricter <c>www.sec.gov</c> host, starving the AI earnings-release path that depends on it.
/// <para>
/// Thread-safe: a single <see cref="SemaphoreSlim"/> both serializes callers and guards the next-slot
/// instant, so concurrent SEC clients are correctly ordered and spaced. Pure of wall-clock — timing comes
/// from the injected <see cref="TimeProvider"/> (deterministic under a fake clock in tests). A zero
/// <see cref="SecRateLimitOptions.MinInterval"/> still serializes (cheap) but adds no delay, reproducing
/// un-paced throughput.
/// </para>
/// </summary>
internal sealed class SecRequestPacer
{
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The earliest instant at which the NEXT request may proceed. Guarded by _gate.
    private DateTimeOffset _nextEarliestUtc = DateTimeOffset.MinValue;

    public SecRequestPacer(SecRateLimitOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (options.MinInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"SEC {nameof(SecRateLimitOptions.MinInterval)} must not be negative; was {options.MinInterval}. "
                    + "A negative pacing interval is nonsensical configuration.");
        }

        _minInterval = options.MinInterval;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Blocks until the caller may issue its SEC request: acquires the shared gate, waits out any time
    /// remaining until <see cref="_nextEarliestUtc"/> so consecutive requests are at least
    /// <see cref="SecRateLimitOptions.MinInterval"/> apart, then reserves the following slot and releases
    /// the gate. Honours cancellation both while waiting on the gate and during the pacing delay.
    /// </summary>
    public async Task WaitTurnAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_nextEarliestUtc > now)
            {
                await Task.Delay(_nextEarliestUtc - now, _timeProvider, ct).ConfigureAwait(false);
                now = _timeProvider.GetUtcNow();
            }

            _nextEarliestUtc = now + _minInterval;
        }
        finally
        {
            _gate.Release();
        }
    }
}
