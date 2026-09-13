namespace Radar.Application.Scoring;

/// <summary>
/// The tunable MAGNITUDE of the same-insider Form 4 collapse (spec 224): the window within which several
/// directional <see cref="Radar.Domain.Signals.SignalType.InsiderBuying"/> filings by ONE reporting owner in
/// ONE direction for ONE company are treated as ONE decision and collapsed to a single representative
/// carrying the aggregate value. Bound from <c>Radar:Scoring:InsiderCollapse:*</c> (exactly parallel to
/// <see cref="MediaCollapseOptions"/>); the collapse *structure* (the bucket key, greedy same-window
/// bucketing, the earliest-member representative rule and the aggregate re-derivation) is the versioned
/// part — <see cref="InsiderActivityCollapse.Version"/> (<c>insider-collapse-v1</c>).
///
/// <para>
/// <b>WHY 30 DAYS, and not the media collapse's 3.</b> The two collapses answer different questions. Media
/// coverage of one event is near-simultaneous — many outlets within hours or a day or two — so 3 days
/// bounds "the same story". An insider selling down a position does it over WEEKS: the spec-224 diagnosis
/// over the live store (2026-09-12) found OOMA's <c>STANG ERIC B</c> filing open-market sales on 06-26 and
/// 07-13 (17 days apart), and ATNI producing 13 negative signals from three sellers at 4.3 filings each
/// across the whole store the spec measured (the spec's §4 expects ~13 in-window units; the spec-224 harness
/// found only 2 of them inside the 60-day window at its 2026-09-12 as-of — the pattern is a store-wide
/// one, and any single window sees a slice of it). A 3-day window would collapse almost none of it; 30 days
/// collapses a multi-week disposal into one decision while still separating a spring sale from an autumn
/// one. It is a config magnitude, hashed into the scoring fingerprint by value, so a different call is a
/// profile edit and a re-stamp, not a code change.
/// </para>
/// <para>
/// <see cref="EventWindowDays"/> is expressed as a raw number of days so it binds cleanly from an
/// <c>IConfiguration</c> scalar (a bare <see cref="TimeSpan"/> does not); <see cref="EventWindow"/> is the
/// derived value the algorithm uses. Immutable so the collapse stays a pure function.
/// </para>
/// </summary>
public sealed record InsiderCollapseOptions
{
    /// <summary>The same-insider collapse window in days (default 30). A non-positive value is meaningless (fails fast).</summary>
    public double EventWindowDays { get; init; } = 30.0;

    /// <summary>The collapse window as a <see cref="TimeSpan"/>, derived from <see cref="EventWindowDays"/>.</summary>
    public TimeSpan EventWindow => TimeSpan.FromDays(EventWindowDays);

    /// <summary>
    /// Fail-fast validation: <see cref="EventWindowDays"/> MUST be strictly positive — a zero/negative window
    /// would collapse nothing meaningfully (every filing its own bucket) or be nonsensical. Called from the
    /// <see cref="InsiderActivityCollapse"/> constructor AND from the DI binder so a misconfiguration fails
    /// fast at startup (mirrors <see cref="MediaCollapseOptions.Validate"/>).
    /// </summary>
    public void Validate()
    {
        if (EventWindowDays <= 0)
        {
            throw new InvalidOperationException(
                $"Radar:Scoring:InsiderCollapse:EventWindowDays must be greater than zero; was {EventWindowDays}. "
                    + "A zero/negative window is meaningless for same-insider filing collapse.");
        }
    }
}
