using System.Text.RegularExpressions;

using Radar.Application.Filings;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// One company-reported reference value as the judge receives it (spec 215 §2): a figure the company
/// itself stated in a filing Radar read, projected from the reported-metrics ledger with its own citable
/// <see cref="ReferenceId"/> (= the ledger record's id). Values are carried AS STATED — no arithmetic, no
/// unit conversion — and the judge is told a reference value is a comparison basis, never news.
/// </summary>
public sealed record NewsJudgmentReferenceValue(
    Guid ReferenceId,
    ReportedMetric Metric,
    string Value,
    string Unit,
    string Period,
    string? PriorValue,
    string? PriorPeriod,
    DateTimeOffset FilingDateUtc,
    string Form,
    string Quote);

/// <summary>The projector's output: the ordered supplied references plus the COUNTED remainder the caps left out.</summary>
public sealed record ReferenceValueProjection(
    IReadOnlyList<NewsJudgmentReferenceValue> References,
    int ReferenceValuesOmitted)
{
    public static ReferenceValueProjection Empty { get; } = new([], 0);
}

/// <summary>
/// SPEC 215 §2 — the pure, static, closed-table projection (<c>reference-projection-v1</c>) of a company's
/// reported-metrics ledger into the judge's input: every ledger record whose <see cref="ReportedMetric"/>
/// is NAMED in at least one supplied statement, most recent first, capped at <see cref="MaxPerMetric"/>
/// per metric and <see cref="MaxPerJudgment"/> per judgment, the remainder COUNTED as omitted. No I/O, no
/// clock, no configuration: the same families and ledger always project the same references (AD-3).
/// <para>
/// <b>The metric-phrase table is the ONE definition of "a statement names this metric"</b> — used here to
/// select, and by <see cref="NewsJudgmentValidator.TrajectoryBasisFor"/> to decide
/// <see cref="NewsTrajectoryBasis.ReferenceSupported"/> (a LevelOnly fact whose statement names the metric
/// of a cited reference). Matching uses the SAME whole-word/phrase boundary rule as
/// <see cref="StatementComparisonClassifier"/> (its shared alternation builder — reuse, not a copy), so a
/// phrase never hits inside another word or a hyphenated compound. The table and the caps ARE the
/// projection's identity: changing either is <c>reference-projection-v2</c>, because <see cref="Version"/>
/// joins the judgment cohort key and is therefore hashed into <c>ScoringConfigVersion</c> through the
/// <c>news=</c> segment.
/// </para>
/// </summary>
public static class ReferenceValueProjector
{
    /// <summary>The projection's version token — joins <see cref="NewsJudgmentContract.CohortKey"/> after <c>comparison=</c>.</summary>
    public const string Version = "reference-projection-v1";

    /// <summary>At most this many ledger records per metric reach the judge (most recent first).</summary>
    public const int MaxPerMetric = 4;

    /// <summary>At most this many reference values reach one judgment in total.</summary>
    public const int MaxPerJudgment = 16;

    /// <summary>
    /// The closed table: the statement phrases that NAME each metric. Whole words/phrases only, matched
    /// case-insensitively with the classifier's boundary rule. Declared in enum order (AD-3).
    /// </summary>
    public static readonly IReadOnlyDictionary<ReportedMetric, IReadOnlyList<string>> MetricPhrases =
        new Dictionary<ReportedMetric, IReadOnlyList<string>>
        {
            [ReportedMetric.Revenue] = ["revenue", "revenues", "sales"],
            [ReportedMetric.NetIncome] = ["net income", "net loss", "net profit", "profit", "earnings"],
            [ReportedMetric.DilutedEps] = ["eps", "earnings per share"],
            [ReportedMetric.GrossMargin] = ["gross margin", "margin", "margins"],
            [ReportedMetric.OperatingIncome] = ["operating income", "operating profit"],
            [ReportedMetric.Backlog] = ["backlog", "order book"],
            [ReportedMetric.CashAndInvestments] =
                ["cash", "cash and investments", "cash and equivalents", "cash and cash equivalents"],
            [ReportedMetric.TotalDebt] = ["debt", "net debt"],
            [ReportedMetric.FreeCashFlow] = ["free cash flow", "cash flow"],
        };

    private static readonly IReadOnlyDictionary<ReportedMetric, Regex> MetricRegexes =
        MetricPhrases.ToDictionary(
            e => e.Key,
            e => StatementComparisonClassifier.WholeWordAlternation(e.Value));

    /// <summary>Whether <paramref name="statement"/> names <paramref name="metric"/> under the closed table.</summary>
    public static bool NamesMetric(string? statement, ReportedMetric metric) =>
        !string.IsNullOrWhiteSpace(statement)
        && MetricRegexes.TryGetValue(metric, out var regex)
        && regex.IsMatch(statement);

    /// <summary>Every metric <paramref name="statement"/> names, in enum order (AD-3).</summary>
    public static IReadOnlyList<ReportedMetric> MetricsNamedIn(string? statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
        {
            return [];
        }

        return
        [
            .. Enum.GetValues<ReportedMetric>().Where(m => MetricRegexes[m].IsMatch(statement)),
        ];
    }

    /// <summary>
    /// Projects <paramref name="ledger"/> against <paramref name="families"/>: records whose metric is
    /// named in ANY supplied statement, ordered <c>FilingDateUtc</c> descending then <c>Id</c>, at most
    /// <see cref="MaxPerMetric"/> per metric and <see cref="MaxPerJudgment"/> in total; everything a cap
    /// excluded is counted in <see cref="ReferenceValueProjection.ReferenceValuesOmitted"/>. An empty
    /// ledger or a family set naming no ledger metric projects <see cref="ReferenceValueProjection.Empty"/>.
    /// </summary>
    public static ReferenceValueProjection Project(
        IReadOnlyList<NewsJudgmentInputFamily> families, IReadOnlyList<ReportedMetricRecord> ledger)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(ledger);

        if (ledger.Count == 0 || families.Count == 0)
        {
            return ReferenceValueProjection.Empty;
        }

        var named = new HashSet<ReportedMetric>();
        foreach (var family in families)
        {
            foreach (var metric in MetricsNamedIn(family.Statement))
            {
                named.Add(metric);
            }
        }

        if (named.Count == 0)
        {
            return ReferenceValueProjection.Empty;
        }

        var candidates = ledger
            .Where(r => named.Contains(r.Metric))
            .OrderByDescending(r => r.FilingDateUtc)
            .ThenBy(r => r.Id)
            .ToList();

        var perMetric = new Dictionary<ReportedMetric, int>();
        var selected = new List<NewsJudgmentReferenceValue>();
        var omitted = 0;
        foreach (var record in candidates)
        {
            var taken = perMetric.GetValueOrDefault(record.Metric);
            if (taken >= MaxPerMetric || selected.Count >= MaxPerJudgment)
            {
                omitted++;
                continue;
            }

            perMetric[record.Metric] = taken + 1;
            selected.Add(new NewsJudgmentReferenceValue(
                ReferenceId: record.Id,
                Metric: record.Metric,
                Value: record.Value,
                Unit: record.Unit,
                Period: record.Period,
                PriorValue: record.PriorValue,
                PriorPeriod: record.PriorPeriod,
                FilingDateUtc: record.FilingDateUtc,
                Form: record.Form,
                Quote: record.Quote));
        }

        return new ReferenceValueProjection(selected, omitted);
    }
}
