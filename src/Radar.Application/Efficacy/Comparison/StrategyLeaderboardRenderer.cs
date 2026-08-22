using System.Globalization;
using System.Text;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// Pure, deterministic rendering of a <see cref="StrategyLeaderboard"/> as CSV (machine) + markdown (human).
/// Culture-invariant, fixed precision, <c>\n</c> line endings, no embedded wall-clock — identical input yields
/// byte-identical output (AD-3).
/// <para>
/// <b>Both artifacts state the honest N and name every dropped strategy.</b> That is a spec-140 requirement,
/// not a nicety: a leaderboard that hides how many strategies it chose from, or which ones it quietly discarded
/// for thin data, systematically overstates the winner.
/// </para>
/// <para>
/// <b>Framing (AD-9).</b> The rendered text says what this is — which strategy's scores tracked subsequent
/// price movement more closely — and what it is not: a recommendation, a projection, or financial advice.
/// Radar ranks its own scoring; a human decides what to do about it.
/// </para>
/// </summary>
public sealed class StrategyLeaderboardRenderer
{
    /// <summary>The one-line framing sentence both artifacts carry verbatim.</summary>
    public const string Framing =
        "Research statistic: which strategy's scores tracked subsequent price movement more closely. "
            + "Not a recommendation, not a projection, not financial advice. Radar ranks; a human decides.";

    /// <summary>
    /// The spec-155 scope label: this marginal ranking is DESCRIPTIVE. Marginal rhos are computed over each
    /// strategy's own support and their spread is not an uncertainty estimate of any difference, so nothing
    /// here can support the amended AD-15 claim — only the paired, purged comparison can.
    /// </summary>
    public const string DescriptiveScope =
        "Descriptive only: this marginal ranking answers whether a strategy tracked its outcome at all, not "
            + "whether it beat any comparator, and its moving chronological split is a descriptive backtest, "
            + "not a claim boundary. The paired, purged comparison (strategy-paired-comparison.md) is the "
            + "only result that can support the amended AD-15 claim.";

    private const string CsvHeader =
        "status,rank,strategy,strategiesCompared,strategiesConsidered,"
            + "inSampleRho,inSampleLower95,inSampleUpper95,inSampleObservations,inSampleCompanies,inSampleDates,"
            + "outOfSampleRho,outOfSampleLower95,outOfSampleUpper95,outOfSampleObservations,"
            + "outOfSampleCompanies,outOfSampleDates,observationsWithoutForwardPrice,"
            + "observationsWithPartialWindow,dropReason,metricReason";

    public string RenderCsv(StrategyLeaderboard leaderboard)
    {
        ArgumentNullException.ThrowIfNull(leaderboard);

        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');

        foreach (var row in leaderboard.Rows)
        {
            sb.Append("ranked,");
            sb.Append(Int(row.Rank)).Append(',');
            sb.Append(CsvField.Escape(row.StrategyName)).Append(',');
            sb.Append(Int(leaderboard.StrategiesCompared)).Append(',');
            sb.Append(Int(leaderboard.StrategiesConsidered)).Append(',');
            AppendMetric(sb, row.InSample);
            AppendMetric(sb, row.OutOfSample);
            sb.Append(Int(row.ObservationsWithoutForwardPrice)).Append(',');
            sb.Append(Int(row.ObservationsWithPartialWindow)).Append(',');
            sb.Append(',');                                   // dropReason: empty for a ranked strategy
            sb.Append(MetricReasonToken(row.OutOfSample.Correlation.Reason)).Append('\n');
        }

        foreach (var drop in leaderboard.DroppedStrategies)
        {
            sb.Append("dropped,");
            sb.Append(',');                                   // rank: a dropped strategy has none
            sb.Append(CsvField.Escape(drop.StrategyName)).Append(',');
            sb.Append(Int(leaderboard.StrategiesCompared)).Append(',');
            sb.Append(Int(leaderboard.StrategiesConsidered)).Append(',');
            sb.Append(",,,").Append(Int(drop.InSampleObservations)).Append(",,,");
            sb.Append(",,,").Append(Int(drop.OutOfSampleObservations)).Append(",,,");
            sb.Append(',');                                   // observationsWithoutForwardPrice: not ranked
            sb.Append(',');                                   // observationsWithPartialWindow: not ranked
            sb.Append(DropReasonToken(drop.Reason)).Append(',');
            sb.Append(MetricReasonToken(drop.MetricReason)).Append('\n');
        }

        return sb.ToString();
    }

    public string RenderMarkdown(StrategyLeaderboard leaderboard)
    {
        ArgumentNullException.ThrowIfNull(leaderboard);

        var o = leaderboard.Options;
        var w = leaderboard.Windows;
        var sb = new StringBuilder();

        sb.Append("# Strategy vs price — efficacy leaderboard\n\n");
        sb.Append(Framing).Append("\n\n");
        sb.Append(DescriptiveScope).Append("\n\n");

        sb.Append("## How to read this\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- **Strategies compared (ranked): {leaderboard.StrategiesCompared}.** A leader chosen from many needs a stronger effect than one chosen from few.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Strategies considered: {leaderboard.StrategiesConsidered}; dropped: {leaderboard.DroppedStrategies.Count} (each named below).\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Forward horizon: {o.ForwardHorizonDays} calendar day(s) — a score at D is judged only against price over (D, D+{o.ForwardHorizonDays}]. Price at or before D is never read.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Exit tolerance: {o.ExitToleranceDays} calendar day(s). An observation counts only when its LATEST bar inside (D, D+{o.ForwardHorizonDays}] falls on or after D+{o.ForwardHorizonDays - o.ExitToleranceDays}. One that falls further short is a PARTIAL forward window: it is excluded from the correlation rather than reported as a full {o.ForwardHorizonDays}-day return. The tolerance exists because markets close at weekends and holidays, so the last bar is rarely on the bound itself.\n");
        sb.Append("- \"Observations without a forward price\" and \"observations with a partial forward window\" are counted separately and mean different things: no price at all in the window, versus some price that does not reach the horizon.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Hold-out: the chronologically latest {Percent(o.HoldOutFraction)} of as-of dates. Ranking uses the in-sample window only; the headline number is out-of-sample.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Minimum observations per window: {o.MinimumObservations}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"- As-of dates: {w.TotalAsOfDates} total = {w.InSampleAsOfDates} in-sample ({Range(w.InSampleStart, w.InSampleEnd)}) + {w.OutOfSampleAsOfDates} out-of-sample ({Range(w.OutOfSampleStart, w.OutOfSampleEnd)}). The two sets are disjoint by construction.\n");
        sb.Append("- Metric: Spearman rank correlation between a company's opportunity score at D and its forward return over the horizon, with a two-sided 95% Fisher-z interval. Observations are pooled across companies and dates and are therefore not independent, so the interval is optimistically narrow — treat it as dispersion, not significance.\n\n");

        sb.Append("## Headline (out-of-sample)\n\n");
        if (leaderboard.Headline is { } headline)
        {
            var c = headline.OutOfSample.Correlation;
            var cov = headline.OutOfSample.Coverage;
            sb.Append(CultureInfo.InvariantCulture, $"**{Md(headline.StrategyName)}** — out-of-sample rho {Rho(c.Rho)} (95% CI {Rho(c.LowerBound)} to {Rho(c.UpperBound)}) over {cov.Observations} observation(s), {cov.DistinctCompanies} compan(ies), {cov.DistinctAsOfDates} as-of date(s).\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"It ranked 1 of {leaderboard.StrategiesCompared} on the in-sample window; the number above comes from dates the ranking never saw.\n\n");
        }
        else
        {
            sb.Append("No strategy could be ranked — there is not yet enough joined score/price history for a hold-out comparison. Nothing is being claimed.\n\n");
        }

        sb.Append("## Ranking (ordered by in-sample rho; the headline number is out-of-sample)\n\n");
        if (leaderboard.Rows.Count == 0)
        {
            sb.Append("_No strategy met the minimum observation count in both windows._\n\n");
        }
        else
        {
            sb.Append("| rank | strategy | in-sample rho | in-sample 95% CI | in-sample obs (companies × dates) | out-of-sample rho | out-of-sample 95% CI | out-of-sample obs (companies × dates) | observations without a forward price | observations with a partial forward window |\n");
            sb.Append("| ---: | --- | ---: | --- | --- | ---: | --- | --- | ---: | ---: |\n");
            foreach (var row in leaderboard.Rows)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {row.Rank} | {Md(row.StrategyName)} | {Rho(row.InSample.Correlation.Rho)} | {Rho(row.InSample.Correlation.LowerBound)} to {Rho(row.InSample.Correlation.UpperBound)} | {Coverage(row.InSample.Coverage)} | {Rho(row.OutOfSample.Correlation.Rho)} | {Rho(row.OutOfSample.Correlation.LowerBound)} to {Rho(row.OutOfSample.Correlation.UpperBound)} | {Coverage(row.OutOfSample.Coverage)} | {row.ObservationsWithoutForwardPrice} | {row.ObservationsWithPartialWindow} |\n");
            }

            sb.Append('\n');
        }

        // Spec 176: "dropped" here means dropped FROM THIS RANKING, never "failed to score live" — a
        // strategy listed below may be scoring every company on every run while its declared forward-outcome
        // sample is still too young to rank. The heading and the sentence say so; the count, every numeric
        // field and every drop reason are unchanged.
        sb.Append(CultureInfo.InvariantCulture, $"## Dropped from efficacy ranking ({leaderboard.DroppedStrategies.Count})\n\n");
        sb.Append("A strategy listed here may still be scoring every company live; this section means only that its declared forward-outcome sample cannot yet be ranked.\n\n");
        if (leaderboard.DroppedStrategies.Count == 0)
        {
            sb.Append("_None — every strategy considered was ranked._\n");
        }
        else
        {
            sb.Append("| strategy | reason | in-sample obs | out-of-sample obs | metric detail |\n");
            sb.Append("| --- | --- | ---: | ---: | --- |\n");
            foreach (var drop in leaderboard.DroppedStrategies)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {Md(drop.StrategyName)} | {DropReasonToken(drop.Reason)} | {drop.InSampleObservations} | {drop.OutOfSampleObservations} | {MetricReasonToken(drop.MetricReason)} |\n");
            }
        }

        return sb.ToString();
    }

    private static void AppendMetric(StringBuilder sb, StrategyWindowMetric metric)
    {
        var c = metric.Correlation;
        sb.Append(c.IsDefined ? Rho(c.Rho) : string.Empty).Append(',');
        sb.Append(c.IsDefined ? Rho(c.LowerBound) : string.Empty).Append(',');
        sb.Append(c.IsDefined ? Rho(c.UpperBound) : string.Empty).Append(',');
        sb.Append(Int(metric.Coverage.Observations)).Append(',');
        sb.Append(Int(metric.Coverage.DistinctCompanies)).Append(',');
        sb.Append(Int(metric.Coverage.DistinctAsOfDates)).Append(',');
    }

    private static string Coverage(StrategyWindowCoverage coverage) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{coverage.Observations} ({coverage.DistinctCompanies} × {coverage.DistinctAsOfDates})");

    private static string Range(DateOnly? from, DateOnly? to) =>
        from is { } f && to is { } t
            ? f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + ".."
                + t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "none";

    private static string Percent(double fraction) =>
        (fraction * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Rho(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stable machine tokens — the enum name is the contract, not a localized phrase.</summary>
    private static string DropReasonToken(StrategyDropReason reason) => reason switch
    {
        StrategyDropReason.InsufficientInSampleObservations => "insufficient-in-sample-observations",
        StrategyDropReason.InsufficientOutOfSampleObservations => "insufficient-out-of-sample-observations",
        StrategyDropReason.DegenerateInSampleMetric => "degenerate-in-sample-metric",
        StrategyDropReason.DegenerateOutOfSampleMetric => "degenerate-out-of-sample-metric",
        _ => "unknown",
    };

    private static string MetricReasonToken(RankCorrelationUndefinedReason reason) => reason switch
    {
        RankCorrelationUndefinedReason.None => "defined",
        RankCorrelationUndefinedReason.TooFewObservations => "too-few-observations",
        RankCorrelationUndefinedReason.ConstantScores => "constant-scores",
        RankCorrelationUndefinedReason.ConstantReturns => "constant-returns",
        RankCorrelationUndefinedReason.PerfectCorrelation => "perfect-correlation-no-interval",
        _ => "unknown",
    };

    // Escape the markdown table's cell separator so an exotic strategy name cannot break the table. Strategy
    // names are validated storage segments today, so this never fires — it just keeps the artifact robust.
    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
