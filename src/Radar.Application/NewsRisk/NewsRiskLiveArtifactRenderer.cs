using System.Globalization;
using System.Text;

namespace Radar.Application.NewsRisk;

/// <summary>
/// Pure, deterministic markdown rendering of the live news-risk document (spec 179 §7). With multiple
/// readers each company shows EVERY reader's assessment separately, labelled by reader name and exact model
/// id; factual category agreement is displayed, but NO merged risk score, majority vote or combined verdict
/// exists anywhere — reader disagreement is a finding about the readers, not something to average away.
/// </summary>
public static class NewsRiskLiveArtifactRenderer
{
    public static string RenderMarkdown(NewsRiskLiveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"# News-risk shadow read — {document.GeneratedAtUtc:yyyy-MM-dd}"));
        sb.AppendLine();
        sb.AppendLine("> " + document.Caveat);
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Run: `{(document.RunId?.ToString("D") ?? "(unavailable)")}` · selection cutoff: "
                + $"{FormatInstant(document.SelectionAsOfUtc)} · readers: "
                + $"{string.Join("; ", document.Readers)}"));
        sb.AppendLine();

        if (document.Diagnostic is not null)
        {
            sb.AppendLine($"**Diagnostic: {document.Diagnostic}** — no candidates were assessed this run.");
            sb.AppendLine();
        }

        foreach (var company in document.Companies)
        {
            sb.AppendLine($"## {company.CompanyName} ({company.Ticker ?? "—"})");
            sb.AppendLine();
            sb.AppendLine("Selected by: " + string.Join(
                "; ",
                company.Selections.Select(s =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{s.StrategyName} #{s.Rank} (snapshot `{s.ScoreSnapshotId:D}`)"))));
            sb.AppendLine();
            // All three completeness dimensions render explicitly, always (spec 182 §2) — no dimension is
            // ever collapsed into or hidden behind another, and no combination reads as an all-clear.
            sb.AppendLine("Completeness: " + NewsRiskCompletenessDescription.Describe(
                company.ArchiveCapture,
                company.SearchEnumeration,
                company.AssessmentBundle,
                company.Articles.Count,
                company.QualifyingArticleCount));
            if (company.CoverageIssues.Count > 0)
            {
                sb.AppendLine("Coverage issues: " + string.Join("; ", company.CoverageIssues));
            }

            AppendCompanySyndication(sb, company);

            sb.AppendLine();

            if (company.Articles.Count > 0)
            {
                sb.AppendLine("Supplied text:");
                foreach (var article in company.Articles)
                {
                    sb.AppendLine(
                        $"- `{article.ObservationId:D}` [{article.InputKind}, {article.CaptureMode}] "
                            + $"{article.Headline} — {article.Publisher} <{article.Url}>");
                }

                sb.AppendLine();
            }

            foreach (var result in company.ReaderResults)
            {
                sb.AppendLine($"### Reader {result.ReaderName} ({result.Provider}:{result.ModelId})");
                sb.AppendLine();
                sb.Append(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Status: **{result.Status}** · assessment cutoff {FormatInstant(result.AssessmentCutoffUtc)}"));
                if (result.RiskScore is { } score)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $" · risk score {score}"));
                }

                sb.AppendLine();

                // The permanently-narrow absence wording (spec 182 §3): a pure function of this RUN's
                // dimensions and counts, so a cached raw verdict replayed under different coverage
                // circumstances gets THIS run's presentation. Never an all-clear.
                if (result.Status == NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText)
                {
                    sb.AppendLine(NewsRiskCompletenessDescription.NoRiskWording(
                        company.ArchiveCapture,
                        company.SearchEnumeration,
                        company.AssessmentBundle,
                        company.Articles.Count,
                        company.QualifyingArticleCount));
                }

                if (result.Categories.Count > 0)
                {
                    sb.AppendLine("Categories: " + string.Join(", ", result.Categories));
                }

                foreach (var claim in result.Claims)
                {
                    var excerpts = string.Join(" ", claim.Excerpts.Select(e => $"\"{e}\""));
                    var cited = string.Join(", ", claim.ObservationIds.Select(id => $"`{id:D}`"));
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"- {claim.Category} ({claim.Severity}, confidence {claim.Confidence:0.00}) — {excerpts} [{cited}]"));
                }

                if (!string.IsNullOrEmpty(result.Rationale))
                {
                    sb.AppendLine("Rationale: " + result.Rationale);
                }

                foreach (var warning in result.Warnings)
                {
                    sb.AppendLine("- ⚠ " + warning);
                }

                sb.AppendLine();
            }

            AppendJudgments(sb, company);
            AppendCategoryAgreement(sb, company);
            AppendJudgmentCategoryComparison(sb, company);
        }

        AppendSyndicationTotals(sb, document);
        AppendSignalMaterialization(sb, document);

        return sb.ToString();
    }

    /// <summary>
    /// SPEC 195 §2 — the compact per-company pre-collapse syndication line, rendered ONLY when this run
    /// measured a non-zero duplicate count. A measured zero is still written to the JSON (that is where the
    /// honest zero lives); repeating "0 duplicate copies" under every company would bury the companies that
    /// actually syndicated. A `null` count is NOT RECORDED (a v3 document) and renders nothing, so an
    /// accrued artifact re-rendered through this renderer is byte-identical to before.
    /// </summary>
    private static void AppendCompanySyndication(StringBuilder sb, NewsRiskLiveCompany company)
    {
        if (company.SyndicatedDuplicateCount is not { } duplicates || duplicates == 0)
        {
            return;
        }

        var publishers = company.SyndicatedDistinctPublisherCount ?? 0;
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Syndication before collapse: {duplicates} duplicate cop(y/ies) removed by the "
                + $"duplicate-headline collapse, across {publishers} distinct publisher(s) carrying the "
                + $"collapsed stories. Current-run enumeration provenance; not a scoring, cohort, cache or "
                + $"model input."));
    }

    /// <summary>
    /// SPEC 195 §2 — the artifact-level syndication totals, labelled as CURRENT-RUN PRE-COLLAPSE
    /// ENUMERATION PROVENANCE. Rendered only when at least one company carries a recorded measurement, so a
    /// v3 document (every count null) renders a byte-identical artifact.
    /// <para>
    /// <b>Why the publisher figure is named an incidence sum and not a distinct total.</b> Only the
    /// per-company COUNTS reach this renderer — the publisher NAMES are not on the document — so a
    /// globally distinct publisher figure is not computable here: one publisher syndicating a story to three
    /// companies contributes 3 to this sum. Summing and calling the result "distinct publishers" would be a
    /// false label on a real number, so the sum is reported under the name it actually has. Computing a true
    /// global distinct count would mean carrying publisher names onto the document, which is a wider change
    /// than this slice, and inventing one from these counts is not possible.
    /// </para>
    /// </summary>
    private static void AppendSyndicationTotals(StringBuilder sb, NewsRiskLiveDocument document)
    {
        var measured = document.Companies
            .Where(c => c.SyndicatedDuplicateCount is not null)
            .ToList();
        if (measured.Count == 0)
        {
            return;
        }

        var collapsedCopies = measured.Sum(c => c.SyndicatedDuplicateCount ?? 0);
        var publisherIncidence = measured.Sum(c => c.SyndicatedDistinctPublisherCount ?? 0);
        var companiesWithSyndication = measured.Count(c => c.SyndicatedDuplicateCount > 0);

        sb.AppendLine("## Syndication before collapse (current-run enumeration provenance)");
        sb.AppendLine();
        sb.AppendLine(
            "Measured on THIS run's article enumeration, before the duplicate-headline collapse, and "
                + "recorded beside reader results that may themselves be cached. It is never a scoring, "
                + "cohort, cache-key, completeness or model input, and no direction is read into it: forty "
                + "syndicated copies of one story are neither good news nor bad news, they are one story "
                + "carried widely.");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Collapsed copies: {collapsedCopies} across {companiesWithSyndication} of "
                + $"{measured.Count} company(ies) with a recorded measurement."));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Company-publisher incidence sum: {publisherIncidence} — each syndicating publisher counted "
                + $"ONCE PER COMPANY and summed across companies. This is NOT a globally distinct publisher "
                + $"count: only per-company counts are recorded on this artifact, so one publisher carrying "
                + $"a story for three companies contributes 3."));
        sb.AppendLine();
    }

    /// <summary>
    /// SPEC 194 §1.2 — the judgment-signal materialization summary, rendered LAST and only when the step
    /// ran. It is a RUN-level fact (one pass over every judgment), so it sits at the document level rather
    /// than being repeated under each company.
    /// <para>
    /// <b>Additive and trailing by construction:</b> a <c>null</c> summary appends nothing at all, so every
    /// pre-194 composition — and every run with no materializer registered — renders a byte-identical
    /// artifact. Nothing above this line reads the new member.
    /// </para>
    /// <para>
    /// The skip detail is rendered even when it is long, because the named reasons ARE the finding: a run
    /// that grounded no direction has to say which precondition was missing, or "0 materialized" reads as
    /// "the judge found nothing" when it may mean "the provenance chain was incomplete".
    /// </para>
    /// </summary>
    private static void AppendSignalMaterialization(StringBuilder sb, NewsRiskLiveDocument document)
    {
        if (document.SignalMaterialization is not { } summary)
        {
            return;
        }

        sb.AppendLine("## Judgment-derived news signals (spec 194 §1.2)");
        sb.AppendLine();
        sb.AppendLine(
            "One validated presentation-cohort judgment may create ONE directional media-attention signal, "
                + "anchored to the evidence that judgment cited. A later article never inherits it, and the "
                + "signal becomes score-visible only from a later run (its knowledge time is the "
                + "materialization instant, never the judgment's).");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Judgments considered: {summary.JudgmentsConsidered} · eligible: {summary.Eligible} · "
                + $"materialized: {summary.Materialized} · already materialized: "
                + $"{summary.AlreadyMaterialized} · validation-rejected: {summary.ValidationRejected} · "
                + $"not durably persisted: {summary.WriteFailed}"));

        var skips = summary.DescribeSkips();
        sb.AppendLine(skips.Length > 0
            ? "Not materialized, by reason: " + skips
            : "Not materialized, by reason: none.");
        sb.AppendLine();
    }

    /// <summary>
    /// The spec-185 §5 per-company two-stage judgment sections: one block per (judge × stage-1 cohort),
    /// each labelled by judge name and exact model id and rendered independently (cohorts never pool, no
    /// merged verdict). All FIVE completeness dimensions render on every block, the finding-drop accounting
    /// renders beside stage 1's fact-drop count (the extraction-vs-judgment error split), and the
    /// presentation cohort's marker state is stated verbatim. The §3 audited-sample error split is not yet
    /// computable (no audited stage-1 sample exists) — stated as a caveat, never invented.
    /// </summary>
    private static void AppendJudgments(StringBuilder sb, NewsRiskLiveCompany company)
    {
        if (company.Judgments is not { Count: > 0 } judgments)
        {
            return;
        }

        sb.AppendLine("### Two-stage judgment (facts-only judge; exploratory until stage-1 recall is audited)");
        sb.AppendLine();
        if (company.JudgmentMarker is { Length: > 0 } marker)
        {
            sb.AppendLine("Leaders marker (presentation cohort only): " + marker);
            sb.AppendLine();
        }

        foreach (var judgment in judgments)
        {
            sb.AppendLine($"#### Judge {judgment.JudgeName} ({judgment.Provider}:{judgment.ModelId}) "
                + $"over stage-1 `{judgment.Stage1CohortKey}`");
            sb.AppendLine();
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"Status: **{judgment.Status}** · judgment `{judgment.JudgmentId:D}`"));
            if (judgment.BusinessTrajectory is { } trajectory)
            {
                sb.Append(string.Create(
                    CultureInfo.InvariantCulture, $" · business trajectory {trajectory}"));
            }

            if (judgment.ChallengeStrength is { } strength)
            {
                sb.Append(string.Create(
                    CultureInfo.InvariantCulture, $" · challenge strength {strength}"));
            }

            sb.AppendLine();
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Completeness: archive capture {judgment.ArchiveCapture} · search enumeration "
                    + $"{judgment.SearchEnumeration} · observation supply {judgment.ObservationSupply} · "
                    + $"typing {judgment.TypingCompleteness} · family bundle {judgment.FamilyBundle}"));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Supplied families: {judgment.Families.Count} · error split — stage-1 facts dropped in "
                    + $"window: {judgment.Stage1FactsDroppedInWindow}; stage-2 findings dropped: "
                    + $"{judgment.FindingsDropped} of {judgment.FindingsTotal}"));

            if (judgment.BusinessTrajectory is not null)
            {
                // Spec 187 §1: the trajectory's own provenance, rendered beside it. A v1 record predates
                // the field entirely — it reads as NOT RECORDED, never as an empty v2 evidence set.
                sb.AppendLine(judgment.TrajectoryFactIds is { } trajectoryFactIds
                    ? trajectoryFactIds.Count > 0
                        ? "Trajectory evidence: "
                            + string.Join(", ", trajectoryFactIds.Select(id => $"`{id:D}`"))
                        : "Trajectory evidence: none cited (an Unknown trajectory establishes no direction)"
                    : "Trajectory evidence: not recorded under news-judgment-v1");
            }

            foreach (var family in judgment.Families)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"- family `{family.FamilyId:D}` (representative fact "
                        + $"`{family.RepresentativeFactId:D}`, {family.MemberCount} member(s), "
                        + $"{family.DistinctPublisherCount} publisher(s))"));
            }

            foreach (var finding in judgment.Findings)
            {
                var cited = string.Join(", ", finding.FactIds.Select(id => $"`{id:D}`"));
                sb.Append(string.Create(
                    CultureInfo.InvariantCulture,
                    $"- {finding.Category} ({finding.Severity}, confidence {finding.Confidence:0.00}) "
                        + $"[{cited}]"));
                if (finding.AttributionCaveat is { Length: > 0 } caveat)
                {
                    sb.Append(" — attribution caveat: " + caveat);
                }

                sb.AppendLine();
            }

            foreach (var reason in judgment.FindingDropReasons)
            {
                sb.AppendLine("- ⚠ dropped: " + reason);
            }

            if (!string.IsNullOrEmpty(judgment.Rationale))
            {
                sb.AppendLine("Rationale: " + judgment.Rationale);
            }

            sb.AppendLine();
        }
    }

    /// <summary>
    /// The spec-185 §3 A/B comparison, DISPLAY only: the single-call read's categories vs each two-stage
    /// cohort's finding categories, side by side (the category-agreement precedent). No merged verdict, no
    /// majority vote, no score — disagreement is a finding about the pipelines, not something to average.
    /// </summary>
    private static void AppendJudgmentCategoryComparison(StringBuilder sb, NewsRiskLiveCompany company)
    {
        if (company.Judgments is not { Count: > 0 } judgments)
        {
            return;
        }

        sb.AppendLine("### Single-call vs two-stage categories (factual, no merged verdict)");
        sb.AppendLine();
        foreach (var reader in company.ReaderResults)
        {
            var categories = reader.Categories.Count > 0
                ? string.Join(", ", reader.Categories)
                : "(none)";
            sb.AppendLine(
                $"Single-call {reader.ReaderName} ({reader.Provider}:{reader.ModelId}): {categories}");
        }

        foreach (var judgment in judgments)
        {
            var categories = judgment.Findings.Count > 0
                ? string.Join(", ", judgment.Findings.Select(f => f.Category).Distinct())
                : "(none)";
            sb.AppendLine(
                $"Two-stage {judgment.JudgeName} ({judgment.Provider}:{judgment.ModelId}) over stage-1 "
                    + $"`{judgment.Stage1CohortKey}`: {categories}");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Factual category agreement across readers — DISPLAY only (which categories every reader found, and
    /// which only some did). Deliberately no merged score, no majority vote, no combined verdict.
    /// </summary>
    private static void AppendCategoryAgreement(StringBuilder sb, NewsRiskLiveCompany company)
    {
        if (company.ReaderResults.Count < 2)
        {
            return;
        }

        var perReader = company.ReaderResults
            .Select(r => (r.ReaderName, Categories: r.Categories.ToHashSet()))
            .ToList();
        var all = perReader.SelectMany(r => r.Categories).Distinct().OrderBy(c => c).ToList();
        if (all.Count == 0)
        {
            return;
        }

        var byEvery = all.Where(c => perReader.All(r => r.Categories.Contains(c))).ToList();
        var bySome = all.Except(byEvery).ToList();

        sb.AppendLine("### Reader agreement (factual, no merged verdict)");
        sb.AppendLine();
        if (byEvery.Count > 0)
        {
            sb.AppendLine("Found by every reader: " + string.Join(", ", byEvery));
        }

        foreach (var category in bySome)
        {
            var names = perReader.Where(r => r.Categories.Contains(category)).Select(r => r.ReaderName);
            sb.AppendLine($"Found only by {string.Join(", ", names)}: {category}");
        }

        sb.AppendLine();
    }

    private static string FormatInstant(DateTimeOffset? instant) =>
        instant is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "(unavailable)";
}
