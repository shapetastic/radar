namespace Radar.Application.Scoring;

/// <summary>
/// Operational scoring parameters (NOT the scoring formula). The window length controls which recent
/// signals feed a snapshot; it is a tunable pipeline knob, not a weight.
/// </summary>
public sealed class ScoringOptions
{
    /// <summary>Length of the recent-signal window. Default 30 days per the pipeline spec.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromDays(30);
}
