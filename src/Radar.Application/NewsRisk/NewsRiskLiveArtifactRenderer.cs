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

            AppendCategoryAgreement(sb, company);
        }

        return sb.ToString();
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
