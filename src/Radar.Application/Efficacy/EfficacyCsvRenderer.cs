using System.Globalization;
using System.Text;

namespace Radar.Application.Efficacy;

/// <summary>
/// Pure, deterministic CSV export of a <see cref="CompanyEfficacySeries"/>: a header row plus one row per
/// <see cref="EfficacyPoint"/>. Culture-invariant formatting, ISO <c>yyyy-MM-dd</c> dates, and an empty cell
/// for a <c>null</c> price field. This is a research/validation dataset (AD-14) — never advice (AD-9); it
/// carries no advice language and embeds no wall-clock, so identical input yields byte-identical output (AD-3).
/// </summary>
public sealed class EfficacyCsvRenderer
{
    // Spec 141: the SERIES key (the strategy name) leads, with the scoringConfigVersion fingerprint kept
    // alongside it as recorded provenance — a consumer grouping the export must group by seriesKey, which a
    // collector toggle cannot move, not by the fingerprint, which is now only an annotation.
    private const string Header =
        "scoreDate,seriesKey,scoringConfigVersion,trajectory,opportunity,attention,evidenceConfidence,velocity,"
            + "priceAsOfDate,priceClose,priceAdjClose";

    public string Render(CompanyEfficacySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');

        foreach (var p in series.Points)
        {
            sb.Append(Date(p.ScoreDate)).Append(',');
            sb.Append(Csv(p.SeriesKey)).Append(',');
            sb.Append(Csv(p.ScoringConfigVersion)).Append(',');
            sb.Append(Int(p.TrajectoryScore)).Append(',');
            sb.Append(Int(p.OpportunityScore)).Append(',');
            sb.Append(Int(p.AttentionScore)).Append(',');
            sb.Append(Int(p.EvidenceConfidenceScore)).Append(',');
            sb.Append(Int(p.SignalVelocityScore)).Append(',');
            sb.Append(p.PriceAsOfDate is { } d ? Date(d) : string.Empty).Append(',');
            sb.Append(Decimal(p.PriceClose)).Append(',');
            sb.Append(Decimal(p.PriceAdjClose)).Append('\n');
        }

        return sb.ToString();
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Decimal(decimal? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;

    // Minimal CSV escaping for the free-text identity fields (series key + fingerprint): quote +
    // double-embedded-quotes only when the value contains a comma, quote, or newline. Neither does today
    // (a strategy name is a validated storage segment), but this keeps the export robust. The rule itself
    // lives in the SHARED CsvField (spec 140 extracted it so the leaderboard export cannot drift from it) —
    // the behaviour here is byte-identical to the inline copy it replaced.
    private static string Csv(string? value) => CsvField.Escape(value);
}
