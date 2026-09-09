using System.Text.RegularExpressions;

using Radar.Application.Filings;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// WHAT a projected reference value IS (spec 216 §1). Values start at 1 so a defaulted zero is UNDEFINED
/// (the strict file-store enum converter refuses it) — a reference whose kind was never recorded must never
/// read as a prior.
/// </summary>
public enum NewsJudgmentReferenceKind
{
    /// <summary>
    /// A figure from an EARLIER accession than the newest one that reported this metric: the company's own
    /// previous statement of the same measure. The newest accession's figure is the CURRENT value and is
    /// never a reference.
    /// </summary>
    Prior = 1,

    /// <summary>
    /// The prior pair the NEWEST filing itself stated ("revenue of $384.0 million, versus $350.1 million in
    /// Q2 FY26"). It is the company's own prior statement and is by construction NOT the current value, so
    /// it is projectable even though its record is the excluded newest one.
    /// </summary>
    StatedPrior = 2,
}

/// <summary>
/// One company-reported reference value as the judge receives it (spec 215 §2): a figure the company
/// itself stated in a filing Radar read, projected from the reported-metrics ledger with its own citable
/// <see cref="ReferenceId"/>. Values are carried AS STATED — no arithmetic, no unit conversion — and the
/// judge is told a reference value is a comparison basis, never news.
/// <para>
/// SPEC 216 §1 adds <see cref="Kind"/>, and the judge is told which kind it sees. For a
/// <see cref="NewsJudgmentReferenceKind.StatedPrior"/> the <see cref="Value"/>/<see cref="Period"/> ARE the
/// prior pair (that is the whole reference) and <see cref="PriorValue"/>/<see cref="PriorPeriod"/> are null,
/// so a reference can never present the same figure twice under two names.
/// </para>
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
    string Quote,
    NewsJudgmentReferenceKind Kind);

/// <summary>
/// The projector's output: the ordered supplied references, the COUNTED remainder the caps left out, and
/// (spec 216 §1/§5) the three counted exclusions — the newest accession per metric, a filing later than the
/// fact it would be compared with, and a ledger file written under a SUPERSEDED policy.
/// </summary>
public sealed record ReferenceValueProjection(
    IReadOnlyList<NewsJudgmentReferenceValue> References,
    int ReferenceValuesOmitted,
    int ReferencesExcludedNewest = 0,
    int ReferencesExcludedLaterThanFact = 0,
    int ReferencesSkippedSupersededPolicy = 0)
{
    public static ReferenceValueProjection Empty { get; } = new([], 0);
}

/// <summary>
/// SPEC 215 §2, CORRECTED BY SPEC 216 §1 — the pure, static, closed-table projection
/// (<c>reference-projection-v2</c>) of a company's reported-metrics ledger into the judge's input. No I/O,
/// no clock, no configuration: the same families and ledger always project the same references (AD-3).
/// <para>
/// <b>A REFERENCE IS PRIOR TO THE FACT IT SUPPORTS — a STRUCTURAL rule, not a timestamp.</b> v1 selected
/// EVERY ledger row whose metric a statement named, with no temporal check at all, while the collection
/// pass writes the newest filing's metrics BEFORE the judge runs in the same pass. So a news fact "backlog
/// $2.5B" was handed the same $2.5B from the same release as its "reference" and could grade
/// <see cref="NewsTrajectoryBasis.ReferenceSupported"/> on its own value. A timestamp test alone does not
/// fix it: a release routinely precedes its own coverage by hours or a day, so "filing date strictly
/// earlier than the article" still admits the current value; and a same-accession exclusion is impossible,
/// because stage-1 news facts carry observation ids and excerpts, never the SEC accession behind a company
/// release. The rule is therefore on the LEDGER'S OWN ORDER:
/// <list type="bullet">
/// <item><b>The newest accession for a metric is never a reference.</b> Per metric, records are ordered by
/// <c>FilingDateUtc</c>, then — deterministically, for same-day filings — by ACCESSION (ordinal string
/// order, which is chronological within a filer's sequence), then by record id; the record from the LATEST
/// accession is the CURRENT value and is EXCLUDED, counted as
/// <see cref="ReferenceValueProjection.ReferencesExcludedNewest"/>. A record becomes eligible only once a
/// LATER accession has reported the same metric — so a metric reported ONCE is never a reference, which is
/// the honest state of a young ledger.</item>
/// <item><b>A complete prior pair from the newest filing IS projectable, separately</b>, as
/// <see cref="NewsJudgmentReferenceKind.StatedPrior"/> with its own ReferenceId: the release itself stated
/// the comparison, so it is the company's own prior statement and is by construction not the current
/// value.</item>
/// <item><b>A secondary guard on top of the structural rule:</b> a reference whose filing date is LATER
/// than the <see cref="NewsJudgmentInputFamily.ObservedAtUtc"/> of every supplied family that NAMES its
/// metric is excluded and counted as
/// <see cref="ReferenceValueProjection.ReferencesExcludedLaterThanFact"/> — a newer filing may never be
/// compared BACKWARDS against older news. A family whose observation instant is not recorded cannot
/// exclude anything (null means "not recorded", never a fabricated comparison).</item>
/// <item><b>Only the CURRENT policy's ledger files are read</b> (spec 216 §5); records written under a
/// superseded policy are counted as
/// <see cref="ReferenceValueProjection.ReferencesSkippedSupersededPolicy"/> and never mixed in, because the
/// verification rule that admitted them is not the one in force.</item>
/// </list>
/// </para>
/// <para>
/// <b>The metric-phrase table is the ONE definition of "a statement names this metric"</b> — used here to
/// select, and by <see cref="NewsJudgmentValidator.TrajectoryBasisFor"/> to decide
/// <see cref="NewsTrajectoryBasis.ReferenceSupported"/>. Matching uses the SAME whole-word/phrase boundary
/// rule as <see cref="StatementComparisonClassifier"/> (its shared alternation builder — reuse, not a
/// copy), so a phrase never hits inside another word or a hyphenated compound. It is deliberately a
/// DIFFERENT table from <see cref="ReportedMetricSynonyms"/>: that one asks whether a FILING QUOTE names a
/// metric (verification identity, part of the reported-metrics policy), this one asks whether a NEWS
/// STATEMENT does (projection identity). They answer different questions about different text and are
/// versioned separately on purpose. The table, the caps and the eligibility rules ARE this projection's
/// identity: changing any of them is <c>reference-projection-v3</c>, because <see cref="Version"/> joins
/// the judgment cohort key and is therefore hashed into <c>ScoringConfigVersion</c> through the
/// <c>news=</c> segment.
/// </para>
/// </summary>
public static class ReferenceValueProjector
{
    /// <summary>
    /// The projection's version token — joins <see cref="NewsJudgmentContract.CohortKey"/> after
    /// <c>comparison=</c>. Moved to <c>v2</c> by spec 216 §1: the eligibility rules changed, so a v1 and a
    /// v2 judge do not see the same references and must not share a cohort.
    /// </summary>
    public const string Version = "reference-projection-v2";

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
    /// Projects <paramref name="ledger"/> against <paramref name="families"/> under the eligibility rules
    /// in the type remarks, ordered <c>FilingDateUtc</c> descending then <c>Id</c>, at most
    /// <see cref="MaxPerMetric"/> per metric and <see cref="MaxPerJudgment"/> in total; everything a cap
    /// excluded is counted in <see cref="ReferenceValueProjection.ReferenceValuesOmitted"/>, and every
    /// eligibility exclusion is counted on its own axis. An empty ledger, or a family set naming no ledger
    /// metric, projects <see cref="ReferenceValueProjection.Empty"/>.
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

        // Spec 216 §5 — the CURRENT policy only. A superseded-policy file was admitted by a verification
        // rule that is no longer in force, so mixing it in would put figures the current policy would have
        // rejected in front of the judge under the current policy's name.
        var currentPolicy = ledger
            .Where(r => string.Equals(r.Policy, ReportedMetricsPolicy.Version, StringComparison.Ordinal))
            .ToList();
        var supersededPolicy = ledger.Count - currentPolicy.Count;

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
            return ReferenceValueProjection.Empty with
            {
                ReferencesSkippedSupersededPolicy = supersededPolicy,
            };
        }

        var excludedNewest = 0;
        var excludedLaterThanFact = 0;
        var eligible = new List<NewsJudgmentReferenceValue>();

        foreach (var metric in Enum.GetValues<ReportedMetric>())
        {
            if (!named.Contains(metric))
            {
                continue;
            }

            // Ordered OLDEST first under the deterministic (filing date, accession, id) key, so "the
            // newest accession" is unambiguous even for same-day filings by the same filer.
            var forMetric = currentPolicy
                .Where(r => r.Metric == metric)
                .OrderBy(r => r.FilingDateUtc)
                .ThenBy(r => r.Accession, StringComparer.Ordinal)
                .ThenBy(r => r.Id)
                .ToList();
            if (forMetric.Count == 0)
            {
                continue;
            }

            // THE STRUCTURAL RULE. Every record belonging to the LATEST accession for this metric is the
            // current value — accession-scoped, not row-scoped, so a release stating two periods of one
            // metric does not make one of its own rows a reference for the other.
            var newestAccession = forMetric[^1].Accession;
            foreach (var record in forMetric)
            {
                if (string.Equals(record.Accession, newestAccession, StringComparison.Ordinal))
                {
                    excludedNewest++;

                    // …but its OWN stated prior pair is projectable, as its own reference.
                    if (record.StatedPriorIdentity is { } statedPriorId)
                    {
                        eligible.Add(new NewsJudgmentReferenceValue(
                            ReferenceId: statedPriorId,
                            Metric: record.Metric,
                            Value: record.PriorValue!,
                            Unit: record.Unit,
                            Period: record.PriorPeriod!,
                            PriorValue: null,
                            PriorPeriod: null,
                            FilingDateUtc: record.FilingDateUtc,
                            Form: record.Form,
                            Quote: record.Quote,
                            Kind: NewsJudgmentReferenceKind.StatedPrior));
                    }

                    continue;
                }

                eligible.Add(new NewsJudgmentReferenceValue(
                    ReferenceId: record.Id,
                    Metric: record.Metric,
                    Value: record.Value,
                    Unit: record.Unit,
                    Period: record.Period,
                    PriorValue: record.PriorValue,
                    PriorPeriod: record.PriorPeriod,
                    FilingDateUtc: record.FilingDateUtc,
                    Form: record.Form,
                    Quote: record.Quote,
                    Kind: NewsJudgmentReferenceKind.Prior));
            }
        }

        // THE SECONDARY GUARD, per metric: the latest observation instant among the supplied families that
        // NAME this metric. A reference filed after ALL of them could not have informed any of them, so
        // comparing it backwards is excluded and counted. Families with no recorded instant contribute
        // nothing to the bound — null is "not recorded", never a fabricated comparison.
        var latestObservationByMetric = new Dictionary<ReportedMetric, DateTimeOffset>();
        foreach (var family in families)
        {
            if (family.ObservedAtUtc is not { } observedAt)
            {
                continue;
            }

            foreach (var metric in MetricsNamedIn(family.Statement))
            {
                if (!latestObservationByMetric.TryGetValue(metric, out var current) || observedAt > current)
                {
                    latestObservationByMetric[metric] = observedAt;
                }
            }
        }

        var candidates = new List<NewsJudgmentReferenceValue>();
        foreach (var reference in eligible)
        {
            if (latestObservationByMetric.TryGetValue(reference.Metric, out var latestObservation)
                && reference.FilingDateUtc > latestObservation)
            {
                excludedLaterThanFact++;
                continue;
            }

            candidates.Add(reference);
        }

        var perMetric = new Dictionary<ReportedMetric, int>();
        var selected = new List<NewsJudgmentReferenceValue>();
        var omitted = 0;
        foreach (var reference in candidates
            .OrderByDescending(r => r.FilingDateUtc)
            .ThenBy(r => r.ReferenceId))
        {
            var taken = perMetric.GetValueOrDefault(reference.Metric);
            if (taken >= MaxPerMetric || selected.Count >= MaxPerJudgment)
            {
                omitted++;
                continue;
            }

            perMetric[reference.Metric] = taken + 1;
            selected.Add(reference);
        }

        return new ReferenceValueProjection(
            selected, omitted, excludedNewest, excludedLaterThanFact, supersededPolicy);
    }
}
