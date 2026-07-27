namespace Radar.Application.Scoring;

/// <summary>
/// Operational scoring parameters (NOT the scoring formula). The window length controls which recent
/// signals feed a snapshot; it is a tunable pipeline knob, not a weight.
/// <para>
/// <b>It is nonetheless a fingerprint input (spec 148).</b> "Not a weight" governs where it LIVES, not
/// whether it is hashed: a 14-day and a 30-day run produce materially different Trajectory, SignalVelocity
/// and Attention over the same evidence, so two such runs must never share a <c>ScoringConfigVersion</c>.
/// <see cref="ScoringConfigFingerprint"/> folds <see cref="Window"/> by value (as ticks) and
/// <see cref="EffectiveScoringConfig"/> carries it verbatim.
/// </para>
/// <para>
/// EVERY public property here is a fingerprint input — there is exactly one, and
/// <c>ScoringConfigFingerprintTests</c> pins that fact, so adding a second knob fails a test until its
/// author consciously decides whether it is output-affecting.
/// </para>
/// </summary>
public sealed class ScoringOptions
{
    /// <summary>Length of the recent-signal window. Default 30 days per the pipeline spec.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromDays(30);
}
