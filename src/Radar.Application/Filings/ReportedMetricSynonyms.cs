using System.Text.RegularExpressions;

using Radar.Application.NewsRisk.Judgment;

namespace Radar.Application.Filings;

/// <summary>
/// SPEC 216 §3 — the CLOSED synonym table that decides whether a release quote NAMES the metric the model
/// labelled it with. It is part of <see cref="ReportedMetricsPolicy.Version"/> identity: changing a
/// synonym, adding a metric phrase or editing an exclusion changes WHICH figures can be verified, so it
/// is a policy bump, not a silent edit.
/// <para>
/// <b>Why it exists.</b> Under <c>reported-metrics-v1</c> the metric LABEL was trusted: nothing checked
/// that the quote named the metric at all, so a cash figure labelled <c>Backlog</c> verified and reached
/// the judge as a company-reported backlog. The label is the model's word; this table is the code's.
/// </para>
/// <para>
/// <b>Longest phrase wins, across the WHOLE table.</b> Matching runs ONE alternation over every metric
/// phrase AND every exclusion phrase, ordered longest-first through the shared
/// <see cref="StatementComparisonClassifier.WholeWordAlternation"/> builder (reuse, not a copy), so
/// occurrences are non-overlapping and a longer phrase consumes a shorter one:
/// <c>cash flow</c> is <see cref="ReportedMetric.FreeCashFlow"/>'s territory and can never verify
/// <see cref="ReportedMetric.CashAndInvestments"/>; <c>cost of sales</c> and <c>net debt</c> are
/// EXCLUSIONS that map to no metric at all, so they consume the <c>sales</c> / <c>debt</c> inside them and
/// verify nothing.
/// </para>
/// </summary>
public static class ReportedMetricSynonyms
{
    /// <summary>
    /// The phrases that NAME each metric in a release quote, in enum order (AD-3). Declared exactly as
    /// spec 216 §3 lists them, plus <c>cash flow</c> on <see cref="ReportedMetric.FreeCashFlow"/> — the
    /// spec names that phrase as FreeCashFlow's territory in the negative-synonym rule, and the projector's
    /// own metric-phrase table already carries it.
    /// </summary>
    public static readonly IReadOnlyDictionary<ReportedMetric, IReadOnlyList<string>> Phrases =
        new Dictionary<ReportedMetric, IReadOnlyList<string>>
        {
            [ReportedMetric.Revenue] = ["revenue", "revenues", "net sales", "sales"],
            [ReportedMetric.NetIncome] = ["net income", "net earnings"],
            [ReportedMetric.DilutedEps] =
                ["diluted earnings per share", "diluted eps", "per diluted share"],
            [ReportedMetric.GrossMargin] = ["gross margin"],
            [ReportedMetric.OperatingIncome] = ["operating income", "income from operations"],
            [ReportedMetric.Backlog] = ["backlog", "order backlog", "backlog of"],
            [ReportedMetric.CashAndInvestments] =
                ["cash", "cash and cash equivalents", "cash and investments"],
            [ReportedMetric.TotalDebt] = ["debt", "borrowings"],
            [ReportedMetric.FreeCashFlow] = ["free cash flow", "cash flow"],
        };

    /// <summary>
    /// Phrases that map to NO metric. They exist ONLY to be matched — because the alternation is
    /// longest-first and non-overlapping, an exclusion consumes the shorter metric phrase inside it, which
    /// is what makes "cost of sales" fail <see cref="ReportedMetric.Revenue"/>, "net debt" fail
    /// <see cref="ReportedMetric.TotalDebt"/> and "gross profit" fail
    /// <see cref="ReportedMetric.GrossMargin"/> (spec 216 §3's mandatory negative cases). Nothing here is
    /// silently dropped: an entry whose only candidate occurrence was an exclusion is counted as
    /// <c>DroppedMetricNotInQuote</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedPhrases =
    [
        "cost of sales",
        "cost of revenue",
        "cost of revenues",
        "gross profit",
        "net debt",
    ];

    /// <summary>Every phrase in the table, longest-first, mapped to the metric it names (null = exclusion).</summary>
    private static readonly IReadOnlyDictionary<string, ReportedMetric?> PhraseOwner = BuildOwners();

    private static readonly Regex AllPhrases = StatementComparisonClassifier.WholeWordAlternation(
        [.. PhraseOwner.Keys]);

    private static readonly Regex WhitespaceRuns = new(
        @"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Every NON-OVERLAPPING phrase occurrence in <paramref name="text"/> that names
    /// <paramref name="metric"/>, in position order. Empty when the metric is never named — including when
    /// every candidate occurrence was consumed by a longer phrase belonging to another metric or to
    /// <see cref="ExcludedPhrases"/>.
    /// </summary>
    public static IReadOnlyList<(int Index, int Length)> Occurrences(string? text, ReportedMetric metric)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<(int Index, int Length)>();
        foreach (Match match in AllPhrases.Matches(text))
        {
            // The alternation is case-insensitive and its internal spaces match ANY whitespace run, so the
            // matched text is whatever the release printed: lower-case it and collapse its whitespace back
            // onto the canonical single-spaced declarations above before looking the owner up.
            var canonical = WhitespaceRuns.Replace(match.Value, " ").ToLowerInvariant();
            if (PhraseOwner.TryGetValue(canonical, out var owner) && owner == metric)
            {
                found.Add((match.Index, match.Length));
            }
        }

        return found;
    }

    /// <summary>Whether <paramref name="text"/> names <paramref name="metric"/> under the closed table.</summary>
    public static bool Names(string? text, ReportedMetric metric) => Occurrences(text, metric).Count > 0;

    /// <summary>
    /// The canonical, order-independent rendering of the table — the string a future policy-identity test
    /// can compare against so a synonym edit cannot ship without a policy bump being noticed.
    /// </summary>
    public static string CanonicalDescriptor() =>
        string.Join(
            ';',
            Enum.GetValues<ReportedMetric>()
                .Select(m => $"{m}={string.Join(',', Phrases[m].Order(StringComparer.Ordinal))}")
                .Append($"excluded={string.Join(',', ExcludedPhrases.Order(StringComparer.Ordinal))}"));

    private static Dictionary<string, ReportedMetric?> BuildOwners()
    {
        var owners = new Dictionary<string, ReportedMetric?>(StringComparer.Ordinal);
        foreach (var metric in Enum.GetValues<ReportedMetric>())
        {
            foreach (var phrase in Phrases[metric])
            {
                // A phrase may name exactly one metric: a duplicate would make ownership order-dependent.
                if (!owners.TryAdd(phrase, metric))
                {
                    throw new InvalidOperationException(
                        $"Reported-metric phrase '{phrase}' is declared for more than one metric.");
                }
            }
        }

        foreach (var excluded in ExcludedPhrases)
        {
            if (!owners.TryAdd(excluded, null))
            {
                throw new InvalidOperationException(
                    $"Excluded reported-metric phrase '{excluded}' is also declared as a metric phrase.");
            }
        }

        return owners;
    }
}
