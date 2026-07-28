using System.Globalization;
using System.Text;

using Radar.Application.Scoring;

namespace Radar.ChannelFeasibilityAudit;

/// <summary>
/// Renders the <see cref="AuditReport"/> as deterministic markdown-ish text. Pure formatting plus
/// descriptive aggregation (sums, counts, population variance, distinct-value counts) — it contains no
/// scoring formula and computes no forward outcome. All numbers are culture-invariant (AD-3).
/// </summary>
public static class ChannelFeasibilityReportRenderer
{
    public static string Render(
        AuditReport report, IReadOnlyList<string> extraBudgetLabels)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(extraBudgetLabels);

        var sb = new StringBuilder();
        var companies = report.Companies;

        sb.AppendLine("# Spec 158 — channel feasibility characterization (INPUT ONLY)");
        sb.AppendLine();
        sb.AppendLine(
            "**No forward outcome was computed, read or inspected**: no price, no attention after the "
            + "as-of instant, no efficacy statistic (AD-16 §1 intact).");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- As-of instant D: `{report.AsOfUtc:o}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Window: `({report.WindowStartUtc:o}, {report.AsOfUtc:o}]` ({report.Window.TotalDays:0} days), known-at `CreatedAtUtc <= D`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Pinning run record: `{ChannelFeasibilityAudit.PinnedRunRecordId}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Companies audited: {companies.Count}");
        sb.AppendLine();

        RenderFunnel(sb, companies);
        RenderChannels(sb, companies);
        RenderBreadth(sb, companies);
        RenderBudget(sb, "filings-led-v11 (predeclared, spec 157 §7)", companies,
            companies.Select(c => c.FilingsLedV11).ToList());

        for (var i = 0; i < extraBudgetLabels.Count; i++)
        {
            var index = i;
            RenderBudget(sb, extraBudgetLabels[i], companies,
                companies.Select(c => c.ExtraBudgets[index]).ToList());
        }

        return sb.ToString();
    }

    private static void RenderFunnel(StringBuilder sb, IReadOnlyList<CompanyAuditResult> companies)
    {
        sb.AppendLine("## Eligibility funnel (global)");
        sb.AppendLine();
        var approved = companies.Sum(c => c.ApprovedInWindow);
        var unresolvable = companies.Sum(c => c.EvidenceUnresolvableSignals);
        var resolved = companies.Sum(c => c.ResolvedBeforeSupersede);
        var afterSupersede = companies.Sum(c => c.AfterSupersede);
        var afterCollapse = companies.Sum(c => c.AfterCollapse);
        var recorded = companies.Sum(c => c.RecordedAttribution);
        var inferred = companies.Sum(c => c.InferredAttribution);
        var unattributed = companies.Sum(c => c.UnattributedAttribution);

        sb.AppendLine(CultureInfo.InvariantCulture, $"| Stage | Signals |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Approved, in-window, known-at D | {approved} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Dropped: evidence-unresolvable (before attribution) | {unresolvable} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Resolved ScoringSignals (before supersede) | {resolved} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| After GuidanceChangeSupersede | {afterSupersede} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| After MediaAttentionCollapse (scored set) | {afterCollapse} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Attribution over resolved inputs: recorded | {recorded} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Attribution over resolved inputs: inferred | {inferred} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Attribution over resolved inputs: unattributed | {unattributed} |");
        sb.AppendLine();

        sb.AppendLine("## Eligibility funnel (per company)");
        sb.AppendLine();
        sb.AppendLine("| Company | Ticker | Approved | Evid-unresolvable (distinct ids) | Resolved | After supersede | After collapse | Recorded | Inferred | Unattributed |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in companies)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Name} | {c.Ticker ?? "—"} | {c.ApprovedInWindow} | {c.EvidenceUnresolvableSignals} ({c.DistinctUnresolvableEvidenceIds}) | {c.ResolvedBeforeSupersede} | {c.AfterSupersede} | {c.AfterCollapse} | {c.RecordedAttribution} | {c.InferredAttribution} | {c.UnattributedAttribution} |");
        }

        sb.AppendLine();
    }

    private static void RenderChannels(StringBuilder sb, IReadOnlyList<CompanyAuditResult> companies)
    {
        sb.AppendLine("## Candidate collector channels — v11 structural inputs over the audited companies");
        sb.AppendLine();
        sb.AppendLine(
            "Directional activity mass is the v11 rule (`DirectionalMasses(...).Total`; Neutral excluded). "
            + "`Score > 0` means net-positive preponderance — `max(0, preponderance)` floors net-negative at "
            + "0, and this is saturation-independent, so no channel score is fabricated for collectors "
            + "without a declared saturation. Variances are population variances over all audited companies "
            + "(zeros included). Distinct pairs counts distinct `(directional mass, preponderance)` values "
            + "(round-trip formatting).");
        sb.AppendLine();
        sb.AppendLine("| Collector | Companies with signals | With directional mass | Score > 0 (net-positive) | all-neutral | balanced | net-negative | Σ directional mass | Var(mass) | Var(preponderance) | Distinct (mass, prep) pairs | Recorded sigs | Inferred sigs |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var collector in ChannelFeasibilityAudit.CandidateCollectors)
        {
            var rows = companies
                .Select(c => c.Channels.Single(ch => ch.Collector == collector))
                .ToList();

            var withSignals = rows.Count(r => r.SignalCount > 0);
            var withMass = rows.Count(r => r.DirectionalActivityMass > 0);
            var netPositive = rows.Count(r => r.DirectionState == ChannelDirectionState.Positive);
            var allNeutral = rows.Count(r =>
                r.SignalCount > 0 && r.DirectionState == ChannelDirectionState.None);
            var balanced = rows.Count(r => r.DirectionState == ChannelDirectionState.Balanced);
            var netNegative = rows.Count(r => r.DirectionState == ChannelDirectionState.Negative);
            var totalMass = rows.Sum(r => r.DirectionalActivityMass);
            var varMass = PopulationVariance(rows.Select(r => r.DirectionalActivityMass));
            var varPrep = PopulationVariance(rows.Select(r => r.Preponderance));
            var distinctPairs = rows
                .Select(r => R(r.DirectionalActivityMass) + "|" + R(r.Preponderance))
                .Distinct(StringComparer.Ordinal)
                .Count();
            var recordedSignals = rows.Sum(r => r.RecordedSignals);
            var inferredSignals = rows.Sum(r => r.InferredSignals);

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {collector} | {withSignals} | {withMass} | {netPositive} | {allNeutral} | {balanced} | {netNegative} | {totalMass:0.###} | {varMass:0.####} | {varPrep:0.######} | {distinctPairs} | {recordedSignals} | {inferredSignals} |");
        }

        sb.AppendLine();

        // Name the net-positive companies per channel — the headline feasibility detail, since these are the
        // only companies a v11 channel on that collector would score above 0.
        foreach (var collector in ChannelFeasibilityAudit.CandidateCollectors)
        {
            var positives = companies
                .Select(c => (Company: c, Reading: c.Channels.Single(ch => ch.Collector == collector)))
                .Where(x => x.Reading.DirectionState == ChannelDirectionState.Positive)
                .ToList();
            if (positives.Count == 0)
            {
                continue;
            }

            var positiveList = string.Join(", ", positives.Select(x => string.Create(
                CultureInfo.InvariantCulture,
                $"{x.Company.Ticker ?? x.Company.Name} (mass {x.Reading.DirectionalActivityMass:0.###}, prep {x.Reading.Preponderance:0.###})")));
            sb.AppendLine(CultureInfo.InvariantCulture, $"Net-positive on `{collector}`: {positiveList}");
            sb.AppendLine();
        }
    }

    private static void RenderBreadth(StringBuilder sb, IReadOnlyList<CompanyAuditResult> companies)
    {
        sb.AppendLine("## §3 positive-only breadth channel / §5 breadth answer");
        sb.AppendLine();

        var nonZeroReach = companies.Count(c => c.Breadth.PositiveReach > 0);
        var sumPublishers = companies.Sum(c => c.Breadth.DistinctPositivePublishersPreCollapse);
        var globalPublishers = companies
            .SelectMany(c => c.Breadth.PositivePublisherNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var varReach = PopulationVariance(companies.Select(c => c.Breadth.PositiveReach));
        var distinctReach = companies
            .Select(c => R(c.Breadth.PositiveReach))
            .Distinct(StringComparer.Ordinal)
            .Count();

        sb.AppendLine(CultureInfo.InvariantCulture, $"- Companies with non-zero §3-narrowed breadth reach: **{nonZeroReach}**");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Distinct positive-carrying third-party publishers, cross-company SUM: **{sumPublishers}**");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Distinct positive-carrying third-party publishers, globally de-duplicated: **{globalPublishers.Count}**{(globalPublishers.Count > 0 ? " (" + string.Join(", ", globalPublishers) + ")" : string.Empty)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Var(positive reach) across companies: {varReach:0.######}; distinct reach values: {distinctReach}");
        sb.AppendLine();
        sb.AppendLine(
            "First-party RSS press releases are **not** counted as publishers by the existing reach "
            + "computation: `ScoreSignalMath.IsBreadthPublisher` admits only "
            + "`EvidenceSourceTypes.IsThirdPartyAttentionSource` source types (NewsArticle, SocialMedia, "
            + "ConferenceMention) — `PressRelease` and `Filing` are first-party and excluded.");
        sb.AppendLine();

        sb.AppendLine("| Company | Ticker | Pos. publishers (post-collapse) | Pos. publishers (pre-collapse) | Pos. media count | Positive reach | Full reach | AttentionScore@D | Notedness discount |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var c in companies)
        {
            var b = c.Breadth;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Name} | {c.Ticker ?? "—"} | {b.DistinctPositivePublishersPostCollapse} | {b.DistinctPositivePublishersPreCollapse} | {b.PositiveMediaCount} | {b.PositiveReach:0.####} | {b.FullReach:0.####} | {b.AttentionScore} | {b.NotednessDiscount:0.###} |");
        }

        sb.AppendLine();
    }

    private static void RenderBudget(
        StringBuilder sb,
        string label,
        IReadOnlyList<CompanyAuditResult> companies,
        IReadOnlyList<BudgetEvaluation> evaluations)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## §6 in-memory budget evaluation — {label}");
        sb.AppendLine();

        var scores = evaluations.Select(e => e.OpportunityScore).ToList();
        var above0 = scores.Count(s => s > 0);
        var distinct = scores.Distinct().Count();
        var largestTie = scores.GroupBy(s => s).Max(g => g.Count());
        var variance = PopulationVariance(scores.Select(s => (double)s));

        sb.AppendLine(CultureInfo.InvariantCulture, $"- Companies with integer OpportunityScore > 0: **{above0}** of {scores.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Distinct integer scores: **{distinct}**; largest tie-group: **{largestTie}**");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Cross-company variance of the integer score: {variance:0.####}");
        sb.AppendLine();
        sb.AppendLine("| Company | Ticker | Composite | OpportunityScore | Channel scores |");
        sb.AppendLine("|---|---|---|---|---|");
        for (var i = 0; i < companies.Count; i++)
        {
            var e = evaluations[i];
            var channelText = string.Join(
                ", ",
                e.ChannelScores.Select(ch => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ch.Channel} {ch.Score:0.####} ({ch.SignalCount} sig{(ch.Dark ? ", dark" : string.Empty)})")));
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {companies[i].Name} | {companies[i].Ticker ?? "—"} | {e.Composite:0.####} | {e.OpportunityScore} | {channelText} |");
        }

        sb.AppendLine();
    }

    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static double PopulationVariance(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        var mean = list.Average();
        return list.Sum(v => (v - mean) * (v - mean)) / list.Count;
    }
}
