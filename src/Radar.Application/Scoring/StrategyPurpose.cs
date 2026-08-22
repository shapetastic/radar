namespace Radar.Application.Scoring;

/// <summary>
/// The declared REPORTING purpose of a scoring strategy (spec 176): is this arm a research hypothesis Radar
/// is genuinely exploring, or a deliberately-dumb comparator that exists only to be beaten (AD-15)?
/// <para>
/// Declared explicitly on the strategy entry (<c>Radar:Strategies[i]:Purpose</c>) and <b>never inferred</b>
/// from a <c>baseline-</c> prefix, a <c>-control</c> suffix, a formula type or a hard-coded name set — any
/// of those would create a second, drifting definition of which arms are comparators.
/// </para>
/// <para>
/// <b>Report metadata ONLY.</b> Purpose is not a score input, is excluded from <c>ScoringConfigVersion</c>
/// and from strategy-series identity (<c>ScoreSeriesKey</c>), moves no fingerprint, creates no efficacy
/// segment, and a purpose-only edit must never trip <c>StrategyIdentityGuard</c>. It changes how the weekly
/// report GROUPS the live strategy leaders, nothing else.
/// </para>
/// </summary>
public enum StrategyPurpose
{
    /// <summary>A genuine research arm — a hypothesis Radar is exploring. The default.</summary>
    Research = 0,

    /// <summary>
    /// A diagnostic comparator displayed to show what the research arms may merely be reproducing.
    /// A comparator leader is never a Radar candidate, and the primary strategy may not be one.
    /// </summary>
    Comparator = 1,
}
