namespace Radar.Infrastructure.Sec;

/// <summary>
/// Global SEC HTTP rate-limit configuration, shared by the singleton <see cref="SecRequestPacer"/>.
/// <see cref="MinInterval"/> is the minimum spacing enforced between ANY two SEC (<c>*.sec.gov</c>)
/// requests across the whole process — every collector reader and the earnings-release reader — so the
/// AGGREGATE request rate of a run, not each client in isolation, stays under SEC's per-IP fair-access
/// ceiling (~10 requests/second across all sec.gov hosts). The default 150 ms caps throughput at ~6.7
/// req/s, comfortably under that limit; without it an unpaced collector burst (~3 SEC collectors × N
/// companies) trips SEC's mitigation and blocks <c>www.sec.gov</c>, starving the AI earnings-release path.
/// Set to <see cref="TimeSpan.Zero"/> to disable pacing (un-paced throughput, e.g. for offline tests).
/// </summary>
public sealed class SecRateLimitOptions
{
    /// <summary>
    /// Minimum interval between successive SEC requests process-wide. Must be non-negative (validated by
    /// <see cref="SecRequestPacer"/>). Default 150 ms (~6.7 req/s). <see cref="TimeSpan.Zero"/> disables pacing.
    /// </summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Per-request budget for the ACTUAL SEC fetch, enforced by <see cref="SecRateLimitingHandler"/> and measured
    /// from AFTER the pacer grants the request its turn — so the pacing wait can NEVER count against it, however
    /// deep the shared pacer queue grows as the watch universe scales up. This is why the SEC <c>HttpClient</c>s
    /// set their ambient <see cref="HttpClient.Timeout"/> to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// (the handler owns the timeout instead): with the ambient timeout, pacing wait + fetch shared one budget and
    /// a long queue could time a request out before it was ever sent. Default 100 s (the historical
    /// <see cref="HttpClient.Timeout"/> default), so per-fetch behaviour is unchanged except that the clock now
    /// starts post-pacing. Must be non-negative (validated by <see cref="SecRateLimitingHandler"/>).
    /// <see cref="TimeSpan.Zero"/> disables the handler-owned timeout — the fetch is then bounded only by caller
    /// cancellation (intended for tests, since the ambient <see cref="HttpClient.Timeout"/> is infinite).
    /// </summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(100);
}
