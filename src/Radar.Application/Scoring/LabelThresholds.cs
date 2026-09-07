namespace Radar.Application.Scoring;

/// <summary>
/// The Investigate / Watch Opportunity lines a strategy's labels are minted at (spec 212). A property of the
/// ARM, not of the formula version: every formula emits one headline <c>OpportunityScore</c> on 0–100, but
/// 20 under <c>radar-formula-v11</c> does not mean what 20 means under v8, and two v8 arms can differ as
/// much as two formulas do (<c>default-noattn</c> puts 19.8% of snapshots at or above 40 against
/// <c>default</c>'s 2.1%). So the lines live beside <see cref="StrategyPurpose"/> as report-only strategy
/// metadata, and here in <c>Radar.Application.Scoring</c> rather than <c>Reporting</c> so Scoring never
/// depends back on Reporting.
/// <para>
/// <b>Fixed operating (triage) thresholds, not evidence.</b> A line's operational meaning is "the share of
/// snapshots it puts in front of a reader" — chosen by prevalence once and then FIXED, so a label means the
/// same thing on every report labelled on the same arm and lines. It is connected to no outcome (returns,
/// attention arrival, thesis survival) and says nothing about whether the value is a strong opportunity.
/// </para>
/// <para>
/// <b>Not a fingerprint input.</b> A label line is a presentation decision, not a scoring one: it is
/// excluded from <c>ScoringConfigVersion</c> and strategy-series identity, moves no pin and never trips
/// <c>StrategyIdentityGuard</c> (AD-10 as amended: the fingerprint stamps what changes a SCORE).
/// </para>
/// <para>
/// <see cref="Default"/> (60 / 40) is the ONLY place those two numbers are defined. They are the pre-212
/// constants that <c>WeeklyReportActionPolicyV1</c> carried for every formula; today they apply only when
/// no operating call is declared and the storage primary sets no lines of its own — a declared Lead is
/// REQUIRED to set explicit lines (<c>OperatingCallReducer</c>).
/// </para>
/// </summary>
public sealed record LabelThresholds
{
    /// <summary>The pre-212 lines: Investigate at Opportunity ≥ 60, Watch at ≥ 40.</summary>
    public static LabelThresholds Default { get; } = new(60, 40);

    /// <summary>
    /// Creates a validated pair. Invariant: <c>0 &lt; Watch &lt; Investigate ≤ 100</c> — a Watch line at or
    /// above the Investigate line would make one of the two labels unreachable, a non-positive line would
    /// label every scored company, and Opportunity never exceeds 100.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The invariant does not hold.</exception>
    public LabelThresholds(int investigate, int watch)
    {
        if (investigate > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(investigate),
                investigate,
                "Investigate must be at most 100 (Opportunity is a 0–100 score).");
        }

        if (watch <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(watch),
                watch,
                "Watch must be greater than 0 (a non-positive line would label every scored company).");
        }

        if (watch >= investigate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(watch),
                watch,
                $"Watch ({watch}) must be strictly below Investigate ({investigate}); otherwise one of the "
                    + "two labels is unreachable.");
        }

        Investigate = investigate;
        Watch = watch;
    }

    /// <summary>Opportunity at or above this line labels <c>Investigate</c>.</summary>
    public int Investigate { get; }

    /// <summary>Opportunity at or above this line (and below <see cref="Investigate"/>) labels <c>Watch</c>.</summary>
    public int Watch { get; }
}
