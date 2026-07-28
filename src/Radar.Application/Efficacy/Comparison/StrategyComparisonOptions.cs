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
        int minimumObservations,
        int exitToleranceDays)
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

        if (exitToleranceDays < 0 || exitToleranceDays >= forwardHorizonDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exitToleranceDays),
                exitToleranceDays,
                "Radar:Efficacy:Comparison:ExitToleranceDays must be at least 0 and strictly less than "
                    + "ForwardHorizonDays (which is "
                    + forwardHorizonDays.ToString(CultureInfo.InvariantCulture)
                    + " here): it is how many calendar days short of D+h the last bar may fall and still count "
                    + "as covering the horizon, so a tolerance at or above the horizon makes the check vacuous "
                    + "— every bar after D would qualify, and a four-day return would again be reported as an "
                    + "h-day one.");
        }

        ForwardHorizonDays = forwardHorizonDays;
        HoldOutFraction = holdOutFraction;
        MinimumObservations = minimumObservations;
        ExitToleranceDays = exitToleranceDays;
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
    /// How many CALENDAR days short of <c>D+h</c> the latest bar in the forward window may fall and still count
    /// as covering the horizon. An observation whose exit bar falls further short than this is
    /// <c>PartialWindow</c> — excluded from the correlation, never relabelled as a full-horizon return (spec
    /// 152). A tolerance is needed at all only because markets close at weekends and holidays, so the last bar
    /// inside <c>(D, D+h]</c> is usually a few days before the bound itself.
    /// <para>
    /// <b>The default of 4 is measured, not guessed.</b> Over the whole live price store as of 2026-07-27
    /// (<c>data/prices/</c>: 43 tickers, 2025-07-03 → 2026-07-27, 11,153 bars) the gap between consecutive bars
    /// was 1 day 77.94%, 2 days 1.16%, 3 days 18.10%, 4 days 2.80% — a MAXIMUM observed gap of 4 calendar days.
    /// Over the 15,334 genuinely-complete 21-day windows in that store (every ticker × every as-of date D whose
    /// <c>D+21</c> still lies within that ticker's bars) the shortfall <c>(D+h) − exitBar.Date</c> was 0 days
    /// 68.57%, 1 day 15.14%, 2 days 14.30%, 3 days 1.98% — a MAXIMUM shortfall of 3 days, exactly what a
    /// maximum gap of 4 implies. The share of those genuinely-complete windows a tolerance would wrongly
    /// discard: 1 ⇒ 16.284%, 2 ⇒ 1.983%, 3 ⇒ 0.000%, 4 ⇒ 0.000%.
    /// </para>
    /// <para>
    /// So 4 = the observed maximum shortfall (3) plus one day of headroom for an unscheduled closure (a storm
    /// or systems halt, which US markets have had), discarding <b>0%</b> of the 15,334 measured complete
    /// windows, while the worst case it still admits covers 17 of 21 days ≈ 81% of the horizon. Re-derive it by
    /// re-running the same two distributions over <c>data/prices/</c> if the store grows materially.
    /// </para>
    /// </summary>
    public int ExitToleranceDays { get; }

    /// <summary>
    /// The defaults the composition root uses when <c>Radar:Efficacy:Comparison</c> omits a key: a 21-calendar-day
    /// forward horizon (≈ one trading month), a 30% chronological hold-out, 20 observations per window, and a
    /// 4-calendar-day exit tolerance (see <see cref="ExitToleranceDays"/> for the measurement behind it).
    /// </summary>
    public static StrategyComparisonOptions Default { get; } = new(21, 0.30, 20, 4);
}
