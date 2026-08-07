using System.Globalization;
using System.Text;

using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Efficacy.DenominatorAudit;

/// <summary>
/// Pure, deterministic rendering of a <see cref="DenominatorAuditReport"/> as CSV (machine, one row per
/// observation) + markdown (human, per-strategy statistics and bin tables). Culture-invariant, fixed
/// precision, <c>\n</c> line endings, no embedded wall-clock — identical input yields byte-identical output
/// (AD-3).
/// <para>
/// <b>Both artifacts carry the honesty statements verbatim</b> (spec 172 §3, non-negotiable):
/// non-independence, the size/coverage confound, and every degeneracy named through the shared
/// rank-correlation vocabulary rather than NaN.
/// </para>
/// </summary>
public sealed class ScoreMoveDenominatorAuditRenderer
{
    /// <summary>The framing sentence both artifacts carry verbatim (AD-9: a diagnostic, never advice).</summary>
    public const string Framing =
        "Read-only diagnostic (spec 172): measures whether score MOVES concentrate where the directional "
            + "evidence base is THIN. It changes no score, ranks no company, and is not a recommendation, "
            + "not a projection, not financial advice.";

    /// <summary>The pooled-observations caveat, in the spec's words.</summary>
    public const string NonIndependence =
        "Observations are NOT independent: they are pooled across companies and dates, so any interval or "
            + "spread is dispersion, not significance.";

    /// <summary>The size/coverage confound, stated so a causal reading is off the table.</summary>
    public const string Confound =
        "Confound, stated plainly: DirectionalCount is partly a proxy for company size and coverage. This "
            + "audit cannot separate \"thin evidence amplifies moves\" from \"small companies move more\"; a "
            + "causal reading of rho would drive the wrong remediation.";

    /// <summary>Which floor applies, pinned: the coefficient-only floor of 2, not the interval floor of 4.</summary>
    public const string RhoFloorNote =
        "Rho is Spearman rank correlation (average ranks) computed by the shared comparison implementation, "
            + "coefficient ONLY — no interval. Its floor is 2 observations; the spec's \"fewer than 4\" floor "
            + "belongs to interval-bearing correlations, which this audit does not compute. Below the floor, "
            + "or on a constant vector, the degeneracy is NAMED rather than rendered as NaN; a perfect "
            + "|rho| = 1 renders as a defined coefficient here because there is no interval to collapse.";

    /// <summary>The pairing rule, pinned in words: consecutive snapshots, not consecutive calendar days.</summary>
    public const string PairingRule =
        "One observation per CONSECUTIVE SNAPSHOT PAIR per company, in as-of order (WindowEndUtc): "
            + "consecutive SNAPSHOTS, not consecutive calendar days — a gap in the as-of dates still pairs "
            + "the neighbouring snapshots. Deltas are later minus earlier; linkCount/directionalCount are the "
            + "LATER snapshot's evidence links.";

    private const string CsvHeader =
        "strategy,companyId,asOfDate,deltaOpportunity,deltaTrajectory,absDeltaOpportunity,linkCount,"
            + "directionalCount";

    public string RenderCsv(DenominatorAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.Append("# ").Append(Framing).Append('\n');
        sb.Append("# ").Append(NonIndependence).Append('\n');
        sb.Append("# ").Append(Confound).Append('\n');
        sb.Append("# ").Append(PairingRule).Append('\n');
        sb.Append("# ").Append(RhoFloorNote).Append('\n');

        foreach (var strategy in report.Strategies)
        {
            sb.Append("# strategy ").Append(strategy.StrategyName)
                .Append(": observations=").Append(Int(strategy.Observations.Count))
                .Append(" companiesWithPairs=").Append(Int(strategy.CompaniesWithPairs))
                .Append(" companiesWalked=").Append(Int(strategy.CompaniesWalked))
                .Append(" rhoAbsDeltaOpportunityVsDirectionalCount=")
                .Append(RhoOrReason(strategy.RhoAbsDeltaVsDirectionalCount))
                .Append(" rhoAbsDeltaOpportunityVsLinkCount=")
                .Append(RhoOrReason(strategy.RhoAbsDeltaVsLinkCount))
                .Append('\n');
        }

        sb.Append(CsvHeader).Append('\n');
        foreach (var strategy in report.Strategies)
        {
            foreach (var o in strategy.Observations)
            {
                sb.Append(CsvField.Escape(o.StrategyName)).Append(',');
                sb.Append(o.CompanyId.ToString("D")).Append(',');
                sb.Append(Date(o.AsOfDate)).Append(',');
                sb.Append(Int(o.DeltaOpportunity)).Append(',');
                sb.Append(Int(o.DeltaTrajectory)).Append(',');
                sb.Append(Int(o.AbsDeltaOpportunity)).Append(',');
                sb.Append(Int(o.LinkCount)).Append(',');
                sb.Append(Int(o.DirectionalCount)).Append('\n');
            }
        }

        return sb.ToString();
    }

    public string RenderMarkdown(DenominatorAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.Append("# Score-move vs evidence-denominator audit\n\n");
        sb.Append(Framing).Append("\n\n");
        sb.Append(NonIndependence).Append("\n\n");
        sb.Append(Confound).Append("\n\n");

        sb.Append("## Method\n\n");
        sb.Append("- ").Append(PairingRule).Append('\n');
        sb.Append("- DirectionalCount counts the later snapshot's evidence links whose contribution reason ")
            .Append("is not \"(Neutral)\". The rule is \"not Neutral\": Positive, Negative, Mixed AND an ")
            .Append("unparseable reason all count as directional — nothing is silently classified into the ")
            .Append("neutral bucket.\n");
        sb.Append("- ").Append(RhoFloorNote).Append('\n');
        sb.Append("- A NEGATIVE rho is the hypothesis: fewer directional signals, larger moves.\n");
        sb.Append("- Median = the middle order statistic (odd count) or the mean of the two middle order ")
            .Append("statistics (even count); p90 = the nearest-rank order statistic at ceil(0.9*n). ")
            .Append("Deterministic closed-form over sorted values (AD-3): no interpolation, no resampling, ")
            .Append("no wall-clock.\n");
        sb.Append("- Bins are fixed and ordered (0, 1, 2, 3, 4+); an empty bin renders with empty ")
            .Append("statistics rather than being dropped.\n\n");

        foreach (var strategy in report.Strategies)
        {
            sb.Append("## ").Append(Md(strategy.StrategyName)).Append("\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"- Observations: {strategy.Observations.Count} consecutive-snapshot pair(s) over {strategy.CompaniesWithPairs} compan(ies) with at least two snapshots, of {strategy.CompaniesWalked} walked.\n");
            sb.Append("- Spearman rho, abs(deltaOpportunity) vs DirectionalCount: ")
                .Append(RhoOrReason(strategy.RhoAbsDeltaVsDirectionalCount)).Append('\n');
            sb.Append("- Spearman rho, abs(deltaOpportunity) vs LinkCount: ")
                .Append(RhoOrReason(strategy.RhoAbsDeltaVsLinkCount)).Append("\n\n");

            sb.Append("| directionalCount | observations | medianAbsDeltaOpportunity | p90AbsDeltaOpportunity |\n");
            sb.Append("| ---: | ---: | ---: | ---: |\n");
            foreach (var bin in strategy.Bins)
            {
                sb.Append("| ").Append(bin.Label)
                    .Append(" | ").Append(Int(bin.Count))
                    .Append(" | ").Append(Stat(bin.MedianAbsDeltaOpportunity))
                    .Append(" | ").Append(Stat(bin.P90AbsDeltaOpportunity))
                    .Append(" |\n");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// A defined coefficient as <c>-0.1234 (n=57)</c>; a degenerate one as its NAMED reason with the honest N
    /// — never NaN, never a fabricated 0.
    /// </summary>
    private static string RhoOrReason(SpearmanRhoResult rho) =>
        rho.IsDefined
            ? string.Create(CultureInfo.InvariantCulture, $"{rho.Rho:0.0000} (n={rho.ObservationCount})")
            : string.Create(CultureInfo.InvariantCulture, $"not defined: {ReasonToken(rho.Reason)} (n={rho.ObservationCount})");

    /// <summary>
    /// The shared rank-correlation vocabulary, with the vector each side maps onto in THIS audit made
    /// explicit: <c>ComputeRho</c> was called with first = |ΔOpportunity| and second = the count vector.
    /// </summary>
    private static string ReasonToken(RankCorrelationUndefinedReason reason) => reason switch
    {
        RankCorrelationUndefinedReason.None => "defined",
        RankCorrelationUndefinedReason.TooFewObservations => "too-few-observations",
        RankCorrelationUndefinedReason.ConstantScores =>
            "constant-scores (the abs-delta vector has no rank variance)",
        RankCorrelationUndefinedReason.ConstantReturns =>
            "constant-returns (the count vector has no rank variance)",
        RankCorrelationUndefinedReason.PerfectCorrelation => "perfect-correlation-no-interval",
        _ => "unknown",
    };

    private static string Stat(double? value) =>
        value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Escape the markdown table/heading separator so an exotic strategy name cannot break the layout.
    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
