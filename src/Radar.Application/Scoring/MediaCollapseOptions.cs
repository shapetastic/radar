namespace Radar.Application.Scoring;

/// <summary>
/// The tunable MAGNITUDE of the same-event media-attention collapse (spec 109): the event window within
/// which many near-simultaneous <see cref="Radar.Domain.Signals.SignalType.MediaAttention"/> signals for one
/// company are treated as coverage of ONE event and collapsed to a single representative. Bound from
/// <c>Radar:Scoring:MediaCollapse:*</c> (like <see cref="ScoringWeights"/>); the collapse *structure* (greedy
/// same-window bucketing, earliest representative) is the versioned part —
/// <see cref="MediaAttentionCollapse.Version"/> (<c>media-collapse-v1</c>).
///
/// <para>
/// <see cref="EventWindowDays"/> is expressed as a raw number of days so it binds cleanly from an
/// <c>IConfiguration</c> scalar (a bare <see cref="TimeSpan"/> does not); <see cref="EventWindow"/> is the
/// derived value the algorithm uses. Immutable so the collapse stays a pure function.
/// </para>
/// </summary>
public sealed record MediaCollapseOptions
{
    /// <summary>The event-collapse window in days (default 3). A non-positive value is meaningless (fails fast).</summary>
    public double EventWindowDays { get; init; } = 3.0;

    /// <summary>The event-collapse window as a <see cref="TimeSpan"/>, derived from <see cref="EventWindowDays"/>.</summary>
    public TimeSpan EventWindow => TimeSpan.FromDays(EventWindowDays);

    /// <summary>
    /// Fail-fast validation: <see cref="EventWindowDays"/> MUST be strictly positive — a zero/negative window
    /// would collapse nothing meaningfully (every signal its own bucket) or be nonsensical. Called from the
    /// <see cref="MediaAttentionCollapse"/> constructor AND from the DI binder so a misconfiguration fails
    /// fast at startup (mirrors <see cref="ScoringWeights.Validate"/>).
    /// </summary>
    public void Validate()
    {
        if (EventWindowDays <= 0)
        {
            throw new InvalidOperationException(
                $"Radar:Scoring:MediaCollapse EventWindowDays must be greater than zero; was {EventWindowDays}. "
                    + "A zero/negative window is meaningless for same-event media collapse.");
        }
    }
}
