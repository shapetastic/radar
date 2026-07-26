namespace Radar.Application.Replay;

/// <summary>
/// The resolved, validated series of historical <b>as-of instants</b> a replay scores at (spec 139). Each
/// point becomes one scoring <c>windowEndUtc</c>, so the series IS the replay's question: "what would this
/// strategy have scored on each of these days, knowing only what Radar knew then?"
/// <para>
/// Deliberately a resolved value type in <c>Radar.Application</c>, not a config shape: <c>IConfiguration</c>
/// never reaches this layer (CLAUDE.md layering), so the Worker parses <c>Radar:Replay:From/To/Step</c> and
/// hands the already-validated series across the boundary.
/// </para>
/// <para>
/// Enumeration is <c>from, from+step, from+2·step, …</c> for every point at or before <c>to</c>, ascending
/// and fully deterministic (AD-3): the same three arguments always produce the same list, which is what makes
/// two identical replays diffable to zero. <c>to</c> is included <b>only</b> when it lands exactly on a step
/// boundary — a trailing partial step is not rounded up into a fabricated extra as-of point, because a
/// scoring instant Radar was not asked for is not a data point.
/// </para>
/// <para>
/// There is deliberately <b>no silent cap</b> on <see cref="Count"/>. The spec is explicit that a large range
/// must not be truncated without saying so, so the size is exposed here for the runner to log up front rather
/// than quietly clamped into a shorter (and silently wrong) series.
/// </para>
/// </summary>
public sealed class ReplaySeries
{
    private ReplaySeries(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan step, IReadOnlyList<DateTimeOffset> points)
    {
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Step = step;
        Points = points;
    }

    /// <summary>The first as-of instant (always the first element of <see cref="Points"/>).</summary>
    public DateTimeOffset FromUtc { get; }

    /// <summary>
    /// The requested upper bound. Equal to the last element of <see cref="Points"/> only when it lands on a
    /// step boundary; otherwise the last point is the greatest boundary at or before it.
    /// </summary>
    public DateTimeOffset ToUtc { get; }

    /// <summary>The spacing between successive as-of instants. Always strictly positive.</summary>
    public TimeSpan Step { get; }

    /// <summary>The as-of instants, ascending, in UTC (zero offset). Never empty.</summary>
    public IReadOnlyList<DateTimeOffset> Points { get; }

    /// <summary>How many as-of points this series will score at. Always at least 1.</summary>
    public int Count => Points.Count;

    /// <summary>
    /// Builds the as-of series for <paramref name="fromUtc"/> … <paramref name="toUtc"/> at
    /// <paramref name="step"/>. Both bounds are normalised to UTC (the instant is preserved; only the offset
    /// representation changes) so every stamped <c>WindowEndUtc</c> is zero-offset exactly as a forward run's
    /// <c>TimeProvider.GetUtcNow()</c> is — the replay⊆forward invariant is about instants, and normalising
    /// removes the only way two callers could describe the same instant differently.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="step"/> is zero or negative — a non-positive step describes no series at all and would
    /// otherwise loop forever.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="toUtc"/> is before <paramref name="fromUtc"/> — an inverted range is a configuration
    /// mistake, and silently yielding an empty replay would look like "no history" rather than "bad input".
    /// </exception>
    public static ReplaySeries Create(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan step)
    {
        if (step <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(step),
                step,
                "A replay step must be strictly positive (e.g. 1 day); a zero or negative step describes no "
                    + "as-of series.");
        }

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();

        if (to < from)
        {
            throw new ArgumentException(
                $"A replay range must end at or after it starts, but 'to' ({to:o}) is before 'from' ({from:o}).",
                nameof(toUtc));
        }

        // Computed arithmetically rather than by repeated addition so the count is exact (no accumulated
        // boundary ambiguity) and the loop cannot overflow past DateTimeOffset.MaxValue: the last offset is
        // bounded by (to - from) by construction.
        var stepCount = (to - from).Ticks / step.Ticks;

        // The capacity is only a growth HINT, so it is bounded — an absurd range must fail on its own terms
        // (or be caught by the runner's up-front "this many points will run" log), never by pre-allocating a
        // multi-gigabyte list here.
        var points = new List<DateTimeOffset>((int)Math.Min(stepCount + 1, 4096));
        for (var i = 0L; i <= stepCount; i++)
        {
            points.Add(from.AddTicks(step.Ticks * i));
        }

        return new ReplaySeries(from, to, step, points);
    }
}
