namespace Radar.Infrastructure.Sec;

/// <summary>
/// Options for the SEC EDGAR earnings-release (EX-99.1) reader's bounded HTTP 429 backoff-retry. The reader
/// fetches two <c>www.sec.gov/Archives/…</c> document pages per candidate 8-K (the index, then the exhibit)
/// right after the collection burst, so it can catch SEC's fair-access 429 under load; these knobs bound a
/// short retry so a transient throttle stops starving the AI directional-filing path (spec 105). Kept separate
/// from <see cref="SecCollectorOptions"/> because they are reader-specific reliability tuning, not collector
/// config. Setting <see cref="MaxRetriesOn429"/> to <c>0</c> restores the reader's single-attempt behaviour
/// (429 → skip). Offline tests pass <see cref="RetryBackoff"/> = <see cref="TimeSpan.Zero"/> so the retry path
/// stays instant.
/// </summary>
public sealed class SecEarningsReleaseReaderOptions
{
    /// <summary>How many times the reader re-issues a request after an HTTP 429 before giving up (default 2).</summary>
    public int MaxRetriesOn429 { get; init; } = 2;

    /// <summary>
    /// Base wait before the FIRST 429 retry; the reader backs off exponentially (base, then 2×base, …). The
    /// default 2s keeps the burst short (SEC recovers quickly, unlike GDELT's long cool-down). Registration
    /// fails fast when negative; tests use <see cref="TimeSpan.Zero"/> to stay instant.
    /// </summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(2);
}
