using System.Globalization;

namespace Radar.Application.Ai;

/// <summary>
/// The deterministic latency summary of ONE pass's provider calls for ONE reader/judge (spec 187 §7):
/// how many hosted calls were spent, their nearest-rank p50/p95, the slowest, and the cumulative time
/// they occupied.
/// <para>
/// <b>Why this exists.</b> The first live typing+judgment run spent roughly five minutes of a 1h03 run on
/// 218 hosted calls, but nothing in the records or the logs made that visible — a slow provider and a slow
/// collector were indistinguishable from the outside. This is pure OBSERVABILITY: it is never persisted
/// into any id, cohort key, family id or scoring fingerprint, and it never influences selection, ordering
/// or any decision (AD-3).
/// </para>
/// </summary>
public sealed record ProviderCallTimingSummary(
    int Calls, double P50Ms, double P95Ms, double MaxMs, double TotalMs)
{
    /// <summary>
    /// The summary of a pass that made NO provider call (every candidate served from cache, exhausted, or
    /// carrying no content). Every duration is zero because zero calls were measured — see
    /// <see cref="Describe"/> for why the rendered form says so explicitly instead of printing "0 ms".
    /// </summary>
    public static readonly ProviderCallTimingSummary NoCalls = new(0, 0, 0, 0, 0);

    /// <summary>
    /// The ONE rendered form, shared by the typing and judgment stage summaries so the two logs cannot
    /// drift apart.
    /// <para>
    /// <b>The zero-call decision, taken deliberately and pinned by test:</b> a pass that made no call
    /// OMITS the percentiles entirely rather than rendering "p50 0.0 ms". A measured zero and an
    /// unmeasured zero are different facts, and printing the latter as the former is exactly the kind of
    /// invented number Radar refuses elsewhere (a score without evidence is not a score).
    /// </para>
    /// </summary>
    public string Describe() => Calls == 0
        ? "0 provider call(s); no call latency measured this pass"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Calls} provider call(s); p50 {P50Ms:F1} ms, p95 {P95Ms:F1} ms, max {MaxMs:F1} ms, "
                + $"total {TotalMs:F1} ms");
}

/// <summary>
/// Accumulates the measured duration of each provider invocation in one pass, for one reader/judge, and
/// answers the rolling questions the bounded progress lines ask (count, mean, current maximum) plus the
/// final <see cref="ProviderCallTimingSummary"/>.
/// <para>
/// Durations MUST come from the injected <see cref="TimeProvider"/>'s MONOTONIC timestamp APIs
/// (<see cref="TimeProvider.GetTimestamp"/> / <see cref="TimeProvider.GetElapsedTime(long)"/>) — never
/// from subtracting two <see cref="DateTimeOffset"/> readings, which a clock adjustment can make negative
/// or absurd, and never from a <c>Stopwatch</c>, which is untestable without a wall-clock sleep.
/// </para>
/// <para>
/// Deliberately NOT thread-safe: spec 187 §7 keeps provider calls SERIAL, and a lock here would quietly
/// imply a concurrency policy this slice does not introduce.
/// </para>
/// </summary>
public sealed class ProviderCallTimings
{
    private readonly List<TimeSpan> _durations = [];
    private TimeSpan _total = TimeSpan.Zero;
    private TimeSpan _max = TimeSpan.Zero;

    /// <summary>How many provider calls have been measured in this pass so far.</summary>
    public int Calls => _durations.Count;

    /// <summary>The cumulative measured provider time so far. Accumulated in TICKS, so it never drifts.</summary>
    public TimeSpan Total => _total;

    /// <summary>The slowest call so far; <see cref="TimeSpan.Zero"/> when no call has been measured.</summary>
    public TimeSpan Max => _max;

    /// <summary>The rolling mean call duration in milliseconds; <c>0</c> when no call has been measured.</summary>
    public double MeanMs => _durations.Count == 0 ? 0 : _total.TotalMilliseconds / _durations.Count;

    /// <summary>
    /// Records one measured provider call. A NEGATIVE duration is rejected rather than clamped: the
    /// monotonic timestamp APIs cannot produce one, so it can only mean the caller measured with the wrong
    /// clock, and silently absorbing that would make the summary a fiction.
    /// </summary>
    public void Record(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        _durations.Add(duration);
        _total += duration;
        if (duration > _max)
        {
            _max = duration;
        }
    }

    /// <summary>
    /// The final summary over the CURRENT pass's in-memory durations only — never over history on disk, so
    /// two processes can never disagree about what "this pass" measured.
    /// </summary>
    public ProviderCallTimingSummary Summarize()
    {
        if (_durations.Count == 0)
        {
            return ProviderCallTimingSummary.NoCalls;
        }

        var ascending = _durations.OrderBy(d => d).ToList();
        return new ProviderCallTimingSummary(
            ascending.Count,
            NearestRankMs(ascending, 50),
            NearestRankMs(ascending, 95),
            _max.TotalMilliseconds,
            _total.TotalMilliseconds);
    }

    /// <summary>
    /// The NEAREST-RANK percentile, stated exactly because a percentile with an unstated definition is an
    /// unreproducible number: sort the durations ASCENDING, take
    /// <c>rank = ceil(percentile / 100 × n)</c>, read the 1-BASED element at that rank, and clamp the rank
    /// to <c>[1, n]</c>. No interpolation, no averaging of neighbours, no randomness — the same duration
    /// multiset always yields the same answer (AD-3), which is what makes it pinnable by test.
    /// </summary>
    /// <param name="ascending">The pass's durations, already sorted ascending.</param>
    /// <param name="percentile">The percentile in <c>[0, 100]</c>.</param>
    public static double NearestRankMs(IReadOnlyList<TimeSpan> ascending, int percentile)
    {
        ArgumentNullException.ThrowIfNull(ascending);
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);

        if (ascending.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile / 100d * ascending.Count);
        rank = Math.Clamp(rank, 1, ascending.Count);
        return ascending[rank - 1].TotalMilliseconds;
    }
}
