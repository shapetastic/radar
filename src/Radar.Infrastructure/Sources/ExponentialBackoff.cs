namespace Radar.Infrastructure.Sources;

/// <summary>
/// Shared bounded exponential backoff math for the readers that own a delayed retry on an HTTP 429
/// (GDELT, the SEC earnings-release reader). Extracted so the one piece both share — the
/// <c>base·2^attempt</c> growth and its overflow-safe cap — lives in a single place (reuse-over-copy,
/// CLAUDE.md 76/77/83); each reader keeps its own retry loop and log wording (genuinely per-source).
/// </summary>
internal static class ExponentialBackoff
{
    /// <summary>
    /// Upper bound on a single backoff delay. The exponential growth (<c>base·2^attempt</c>) would
    /// otherwise overflow <see cref="TimeSpan"/> / exceed <see cref="Task.Delay(TimeSpan, CancellationToken)"/>'s
    /// limit for a large attempt count and throw — breaking a reader's never-throw contract. There is also no
    /// point waiting longer than this in-run; a still-throttled feed is better skipped and retried next run.
    /// (Ported from <c>HttpGdeltNewsReader</c>.)
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Exponential backoff for the Nth (zero-based) 429 retry: <c>base·2^attempt</c>, clamped to
    /// <see cref="MaxDelay"/>. Computed in <see cref="double"/> ticks so a large attempt count can never
    /// overflow <see cref="TimeSpan"/> or exceed <see cref="Task.Delay(TimeSpan, CancellationToken)"/>'s limit
    /// (which would throw and break the never-throw contract). A zero base stays zero (keeps tests instant).
    /// </summary>
    public static TimeSpan Compute(TimeSpan baseDelay, int attempt)
    {
        var ticks = baseDelay.Ticks * Math.Pow(2, attempt);
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
    }
}
