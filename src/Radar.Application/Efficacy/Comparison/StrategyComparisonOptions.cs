using System.Globalization;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The resolved, already-validated knobs of the strategy-vs-price comparison (spec 140). Deliberately plain
/// resolved values: <c>IConfiguration</c> never reaches <c>Radar.Application</c> (CLAUDE.md layering), so the
/// composition root binds <c>Radar:Efficacy:Comparison:*</c> and hands the result in.
/// <para>
/// Validation happens in the constructor so a nonsensical horizon/split fails at startup, naming the key, in
/// preference to silently producing a leaderboard nobody can interpret.
/// </para>
/// </summary>
public sealed class StrategyComparisonOptions
{
    /// <summary>The z multiplier for a two-sided 95% Fisher-z interval (the only interval this slice reports).</summary>
    public const double NormalQuantile95 = 1.959963984540054;

    /// <summary>
    /// The smallest observation count for which a Fisher-z interval exists at all: its standard error is
    /// <c>1/sqrt(n-3)</c>, so <c>n &lt;= 3</c> has no dispersion to report and the spec forbids a bare point
    /// estimate.
    /// </summary>
    public const int MinimumObservationsFloor = 4;

    public StrategyComparisonOptions(
        int forwardHorizonDays,
        double holdOutFraction,
        int minimumObservations)
    {
        if (forwardHorizonDays < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(forwardHorizonDays),
                forwardHorizonDays,
                "Radar:Efficacy:Comparison:ForwardHorizonDays must be at least 1 — the forward window (D, D+h] "
                    + "is what enforces causality, and a non-positive horizon would contain no bar after D.");
        }

        if (!double.IsFinite(holdOutFraction) || holdOutFraction <= 0.0 || holdOutFraction >= 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(holdOutFraction),
                holdOutFraction,
                "Radar:Efficacy:Comparison:HoldOutFraction must be strictly between 0 and 1 (it is the share of "
                    + "the CHRONOLOGICALLY LATEST as-of dates held out of ranking); "
                    + holdOutFraction.ToString("R", CultureInfo.InvariantCulture)
                    + " would leave one of the two windows empty by definition.");
        }

        if (minimumObservations < MinimumObservationsFloor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumObservations),
                minimumObservations,
                $"Radar:Efficacy:Comparison:MinimumObservations must be at least {MinimumObservationsFloor}: the "
                    + "reported dispersion is a Fisher-z interval with standard error 1/sqrt(n-3), which does "
                    + "not exist below that, and a point estimate without a spread is exactly what spec 140 "
                    + "forbids.");
        }

        ForwardHorizonDays = forwardHorizonDays;
        HoldOutFraction = holdOutFraction;
        MinimumObservations = minimumObservations;
    }

    /// <summary>
    /// <c>h</c> in "score at D vs price movement over (D, D+h]", in CALENDAR days (the exit bound is
    /// <c>D.AddDays(h)</c>; which trading bars fall inside it is whatever the price series actually holds).
    /// </summary>
    public int ForwardHorizonDays { get; }

    /// <summary>
    /// The share of DISTINCT as-of dates, taken from the chronologically latest end, reserved as the
    /// out-of-sample window. Ranking never sees these dates; the headline metric is computed only on them.
    /// </summary>
    public double HoldOutFraction { get; }

    /// <summary>
    /// The minimum usable observations a strategy needs in EACH window to be ranked. Below it the strategy is
    /// dropped and NAMED (spec 140's "no silent strategy pruning"), never quietly folded into the ranking.
    /// </summary>
    public int MinimumObservations { get; }

    /// <summary>
    /// The defaults the composition root uses when <c>Radar:Efficacy:Comparison</c> omits a key: a 21-calendar-day
    /// forward horizon (≈ one trading month), a 30% chronological hold-out, and 20 observations per window.
    /// </summary>
    public static StrategyComparisonOptions Default { get; } = new(21, 0.30, 20);
}
