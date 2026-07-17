namespace Radar.Application.Scoring;

/// <summary>
/// The tunable MAGNITUDES of the scoring formula (distinct from ScoringOptions, which holds only the
/// operational Window). Bound from Radar:Scoring:*; injected into the formula, which reads weights from
/// here instead of const fields. Defaults matched the radar-formula-v4 constants at spec 89; the spec-94
/// recalibration then dropped MediaReachWeight 0.25 → 0.10 (de-saturating Attention), so a blank/absent
/// config is now the recalibrated v5 default and no longer byte-identical to v4 — this is intentional (see
/// AD-6 spec-94 refinement), not drift. Immutable → the formula stays a pure function. These are for
/// DELIBERATE, reasoned experiments (run different profiles to compare weightings), NOT for curve-fitting
/// weights to price/backtest outcomes — see the spec's Out of scope.
/// </summary>
public sealed record ScoringWeights
{
    public double RecencyFloor { get; init; } = 0.5;
    public double TrajectoryNeutral { get; init; } = 50.0;
    public double TrajectoryScale { get; init; } = 5.0;
    // radar-formula-v6 corroboration-smoothing constant: the directional mass (≈ one full-strength·full-
    // confidence·full-recency signal) that must accrue before Trajectory swings halfway; larger k damps small
    // directional sets more (a lone dissenter is damped-but-not-zeroed). It is the denominator smoother in
    // T_raw = 10·(Mpos−Mneg)/(Mpos+Mneg+k), so it must be strictly positive (Validate()).
    public double TrajectoryCorroborationK { get; init; } = 10.0;
    public double AttentionHalfSaturation { get; init; } = 3.0;   // v4 value (post spec 88)
    public double MediaReachWeight { get; init; } = 0.10;         // spec 94 recalibration (was 0.25, the v4 value)
    public double QualityPrimarySource { get; init; } = 1.00;
    public double QualityHigh { get; init; } = 0.85;
    public double QualityMedium { get; init; } = 0.60;
    public double QualityLow { get; init; } = 0.35;
    public double QualityUnknown { get; init; } = 0.40;
    public double EcQualityBase { get; init; } = 0.60;
    public double EcQualitySpan { get; init; } = 0.40;
    public double EcDiversityBase { get; init; } = 0.70;
    public double EcDiversitySpan { get; init; } = 0.30;
    public double DiversityTarget { get; init; } = 3.0;
    public double VelocitySmoothing { get; init; } = 10.0;
    public double VelocitySteady { get; init; } = 50.0;
    public double OpportunityAttentionDivisor { get; init; } = 250.0;

    /// <summary>
    /// Fail-fast validation of nonsensical values that would break the math or the [0,100] clamp contract.
    /// The four denominators (<see cref="DiversityTarget"/>, <see cref="OpportunityAttentionDivisor"/>,
    /// <see cref="AttentionHalfSaturation"/>, <see cref="TrajectoryCorroborationK"/>) MUST be strictly
    /// positive; the five quality weights and the
    /// four EC base/span values MUST be non-negative. Deliberately tight (weights are meant to be
    /// experimented with) — it throws only on values that would silently distort scoring. Called from the
    /// formula constructor AND from the DI binder so a misconfiguration fails fast at startup.
    /// </summary>
    public void Validate()
    {
        RequirePositive(DiversityTarget, nameof(DiversityTarget));
        RequirePositive(OpportunityAttentionDivisor, nameof(OpportunityAttentionDivisor));
        RequirePositive(AttentionHalfSaturation, nameof(AttentionHalfSaturation));
        RequirePositive(TrajectoryCorroborationK, nameof(TrajectoryCorroborationK));

        RequireNonNegative(QualityPrimarySource, nameof(QualityPrimarySource));
        RequireNonNegative(QualityHigh, nameof(QualityHigh));
        RequireNonNegative(QualityMedium, nameof(QualityMedium));
        RequireNonNegative(QualityLow, nameof(QualityLow));
        RequireNonNegative(QualityUnknown, nameof(QualityUnknown));

        RequireNonNegative(EcQualityBase, nameof(EcQualityBase));
        RequireNonNegative(EcQualitySpan, nameof(EcQualitySpan));
        RequireNonNegative(EcDiversityBase, nameof(EcDiversityBase));
        RequireNonNegative(EcDiversitySpan, nameof(EcDiversitySpan));
    }

    private static void RequirePositive(double value, string field)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"Radar:Scoring weight {field} must be greater than zero (it is a denominator); was {value}. "
                    + "A zero/negative value would break the scoring math.");
        }
    }

    private static void RequireNonNegative(double value, string field)
    {
        if (value < 0)
        {
            throw new InvalidOperationException(
                $"Radar:Scoring weight {field} must not be negative; was {value}. A negative value would "
                    + "silently distort scoring.");
        }
    }
}
