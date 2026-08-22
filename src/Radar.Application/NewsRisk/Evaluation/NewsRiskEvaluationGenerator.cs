using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.News;
using Radar.Application.Prices;

namespace Radar.Application.NewsRisk.Evaluation;

/// <summary>Which table one evaluator row belongs to — the cohorts NEVER pool (spec 179 §8/§9).</summary>
public enum NewsRiskEvaluationTable
{
    /// <summary>Post-boundary, prospective, complete-coverage, completed, non-development, fully-resolved-window rows.</summary>
    CleanProspective = 0,

    /// <summary>A declared development example (EOSE/CASS/MSEX…): visible, never validation evidence.</summary>
    KnownDevelopmentExample,

    /// <summary>Input included migrated headline-only observations — development table.</summary>
    LegacyHeadlineOnly,

    /// <summary>Input included retrospectively fetched content — development table, never point-in-time.</summary>
    RetrospectiveUrlFetch,

    /// <summary>Excluded from every analytic table, with named reasons.</summary>
    Excluded,
}

/// <summary>One frozen company/run/reader assessment joined (read-only) to its forward outcome.</summary>
public sealed record NewsRiskEvaluationRow(
    NewsRiskAssessmentRecord Assessment,
    NewsRiskEvaluationTable Table,
    IReadOnlyList<string> ExclusionReasons,
    DateOnly? EntryDate,
    double? ForwardReturn21d,
    double? MaxAdverseMove21d);

/// <summary>
/// The read-only frozen-assessment evaluator (spec 179 §9): joins PERSISTED assessments + the committed
/// development declarations + the existing price store, and writes one audit artifact pair. It never selects
/// companies, never fetches a URL and never invokes AI — mechanically, it holds no analyzer, no content
/// reader and no strategy-section dependency (the frozen selection provenance inside each assessment is the
/// only selection it ever sees).
/// </summary>
public interface INewsRiskEvaluationGenerator
{
    Task GenerateAsync(CancellationToken ct);
}

/// <summary>
/// Implementation notes, stated because they are decisions:
/// <list type="bullet">
/// <item><b>Entry anchors at <c>assessmentCutoffUtc</c></b> — never <c>selectionAsOfUtc</c> — because a
/// fetched body retrieved at E &gt; D means the assessment could not have existed before E.</item>
/// <item><b>The forward window reuses <see cref="ForwardReturn.TryCompute"/> verbatim</b> (21 calendar days,
/// spec-152 tolerance 4): entry admission (<c>bar.Date &gt; asOf</c>), partial-window and unresolved-price
/// failures all fail CLOSED through the reused primitive.</item>
/// <item><b>Max adverse move</b> is the minimum close-to-entry relative move over the SAME resolved window,
/// from the SAME resolved entry close, computed only when the forward return itself resolved (a complete
/// window is required).</item>
/// <item><b>Associations use ThesisChallenged rows only</b> (they are the rows carrying a
/// <c>RiskScore</c>); NoRiskFound rows appear in the flagged/non-flagged descriptive split instead — a
/// number is never invented for them.</item>
/// <item><b>Reader cohorts never pool</b>: every table and every association is per cohort key.</item>
/// </list>
/// </summary>
public sealed class NewsRiskEvaluationGenerator : INewsRiskEvaluationGenerator
{
    /// <summary>The fixed 21-calendar-day forward horizon (spec 179 §9).</summary>
    public const int HorizonDays = 21;

    /// <summary>The spec-152 measured exit tolerance (4 calendar days), stated at the call site as ForwardReturn requires.</summary>
    public const int ExitToleranceDays = 4;

    /// <summary>The §1 evaluator caveat, verbatim — carried by every evaluation artifact.</summary>
    public const string EvaluatorCaveat =
        "This is exploratory development evidence, not an AD-15 or AD-16 result. Retrospectively retrieved "
            + "content is reported separately and is never treated as point-in-time content at the "
            + "article's publication date.";

    private readonly INewsRiskAssessmentStore _assessmentStore;
    private readonly INewsRiskDevelopmentExampleSource _developmentExamples;
    private readonly INewsProspectiveBoundaryReader _boundaryReader;
    private readonly IPriceHistoryStore _priceStore;
    private readonly INewsRiskArtifactStore _artifactStore;
    private readonly ILogger<NewsRiskEvaluationGenerator> _logger;

    public NewsRiskEvaluationGenerator(
        INewsRiskAssessmentStore assessmentStore,
        INewsRiskDevelopmentExampleSource developmentExamples,
        INewsProspectiveBoundaryReader boundaryReader,
        IPriceHistoryStore priceStore,
        INewsRiskArtifactStore artifactStore,
        ILogger<NewsRiskEvaluationGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(assessmentStore);
        ArgumentNullException.ThrowIfNull(developmentExamples);
        ArgumentNullException.ThrowIfNull(boundaryReader);
        ArgumentNullException.ThrowIfNull(priceStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(logger);

        _assessmentStore = assessmentStore;
        _developmentExamples = developmentExamples;
        _boundaryReader = boundaryReader;
        _priceStore = priceStore;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    public async Task GenerateAsync(CancellationToken ct)
    {
        try
        {
            var assessments = await _assessmentStore.GetAllAsync(ct).ConfigureAwait(false);
            var examples = await _developmentExamples.GetAllAsync(ct).ConfigureAwait(false);
            var boundary = await _boundaryReader.ReadBoundaryAsync(ct).ConfigureAwait(false);

            var rows = new List<NewsRiskEvaluationRow>(assessments.Count);
            foreach (var assessment in assessments)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(await BuildRowAsync(assessment, examples, boundary, ct).ConfigureAwait(false));
            }

            var markdown = RenderMarkdown(rows, examples, boundary);
            var csv = RenderCsv(rows);
            await _artifactStore.WriteEvaluationAsync(markdown, csv, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "News-risk evaluation written: {Rows} row(s), {Clean} clean prospective, "
                    + "{Development} development, {Excluded} excluded.",
                rows.Count,
                rows.Count(r => r.Table == NewsRiskEvaluationTable.CleanProspective),
                rows.Count(r => r.Table
                    is NewsRiskEvaluationTable.KnownDevelopmentExample
                    or NewsRiskEvaluationTable.LegacyHeadlineOnly
                    or NewsRiskEvaluationTable.RetrospectiveUrlFetch),
                rows.Count(r => r.Table == NewsRiskEvaluationTable.Excluded));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Read-only audit: a failure here must never abort the surrounding run.
            _logger.LogError(ex, "News-risk evaluation failed; no artifact was written.");
        }
    }

    private async Task<NewsRiskEvaluationRow> BuildRowAsync(
        NewsRiskAssessmentRecord assessment,
        IReadOnlyList<NewsRiskDevelopmentExample>? examples,
        NewsObservationBoundary? boundary,
        CancellationToken ct)
    {
        // Outcome join first (computed for development rows too — their tables display outcomes; they just
        // never pool with the clean cohort).
        DateOnly? entryDate = null;
        double? forwardReturn = null;
        double? maxAdverse = null;
        var outcomeReasons = new List<string>();

        if (string.IsNullOrWhiteSpace(assessment.Ticker))
        {
            outcomeReasons.Add("no-ticker");
        }
        else
        {
            var history = await _priceStore.ReadAsync(assessment.Ticker, ct).ConfigureAwait(false);
            if (history is null || history.Bars.Count == 0)
            {
                outcomeReasons.Add("no-price-history");
            }
            else
            {
                // Entry anchors at the ASSESSMENT cutoff (never selection): the UTC calendar date of the
                // instant the assessment's inputs were complete.
                var asOf = DateOnly.FromDateTime(assessment.AssessmentCutoffUtc.UtcDateTime);
                var forward = ForwardReturn.TryCompute(history.Bars, asOf, HorizonDays, ExitToleranceDays);
                if (!forward.IsDefined)
                {
                    outcomeReasons.Add("forward-window-" + forward.Reason);
                }
                else
                {
                    entryDate = forward.EntryDate;
                    forwardReturn = forward.Value;
                    maxAdverse = MaxAdverseMove(history.Bars, asOf, forward.EntryDate!.Value);
                }
            }
        }

        // Table assignment — development declarations FIRST, fail closed when they are unavailable.
        var reasons = new List<string>(outcomeReasons);
        NewsRiskEvaluationTable table;
        if (examples is null)
        {
            reasons.Add("development-declarations-unavailable");
            table = NewsRiskEvaluationTable.Excluded;
        }
        else if (assessment.Ticker is not null && examples.Any(
            e => string.Equals(e.Ticker, assessment.Ticker, StringComparison.OrdinalIgnoreCase)))
        {
            table = NewsRiskEvaluationTable.KnownDevelopmentExample;
        }
        else if (assessment.Observations.Any(
            o => o.CaptureMode == NewsObservationCaptureMode.RetrospectiveUrlFetch))
        {
            table = NewsRiskEvaluationTable.RetrospectiveUrlFetch;
        }
        else if (assessment.Observations.Any(
            o => o.CaptureMode == NewsObservationCaptureMode.LegacyHeadlineOnly))
        {
            table = NewsRiskEvaluationTable.LegacyHeadlineOnly;
        }
        else
        {
            // The clean prospective gates (spec 179 §9), every one named on failure.
            if (boundary is null)
            {
                reasons.Add("no-prospective-boundary");
            }
            else if (assessment.AssessmentCutoffUtc < boundary.FirstProspectiveCaptureAsOfUtc)
            {
                reasons.Add("before-prospective-boundary");
            }

            if (!assessment.CoverageComplete)
            {
                reasons.Add("coverage-incomplete");
            }

            if (!assessment.IsCompletedAnalysis
                || assessment.Status is NewsRiskAssessmentStatus.ValidationFailed
                    or NewsRiskAssessmentStatus.InsufficientContent)
            {
                reasons.Add("assessment-not-completed-validated: " + assessment.Status);
            }

            if (forwardReturn is null)
            {
                // The named outcome reason is already in the list.
                if (outcomeReasons.Count == 0)
                {
                    reasons.Add("forward-window-unresolved");
                }
            }

            table = reasons.Count == 0
                ? NewsRiskEvaluationTable.CleanProspective
                : NewsRiskEvaluationTable.Excluded;
        }

        return new NewsRiskEvaluationRow(assessment, table, reasons, entryDate, forwardReturn, maxAdverse);
    }

    /// <summary>
    /// The 21-day maximum adverse close move from the resolved entry close: the minimum of
    /// <c>(price − entry) / entry</c> over bars STRICTLY AFTER the entry date within <c>(asOf, asOf+21]</c>.
    /// Uses the same adjusted-close-with-fallback price rule as <see cref="ForwardReturn"/>. 0.0 means "never
    /// closed below entry"; only called once the forward window has resolved as complete.
    /// </summary>
    private static double MaxAdverseMove(
        IReadOnlyList<PriceBar> bars, DateOnly asOf, DateOnly entryDate)
    {
        var exitBound = asOf.AddDays(HorizonDays);
        decimal? entryPrice = null;
        foreach (var bar in bars)
        {
            if (bar.Date == entryDate)
            {
                entryPrice = Price(bar);
                break;
            }
        }

        if (entryPrice is not { } entry || entry <= 0m)
        {
            return 0.0;
        }

        var worst = 0.0;
        foreach (var bar in bars)
        {
            if (bar.Date <= entryDate || bar.Date > exitBound)
            {
                continue;
            }

            var move = (double)((Price(bar) - entry) / entry);
            if (move < worst)
            {
                worst = move;
            }
        }

        return worst;
    }

    private static decimal Price(PriceBar bar) => bar.AdjClose > 0m ? bar.AdjClose : bar.Close;

    private string RenderMarkdown(
        IReadOnlyList<NewsRiskEvaluationRow> rows,
        IReadOnlyList<NewsRiskDevelopmentExample>? examples,
        NewsObservationBoundary? boundary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# News-risk frozen-assessment evaluation");
        sb.AppendLine();
        sb.AppendLine("> " + EvaluatorCaveat);
        sb.AppendLine();
        sb.AppendLine(
            "No pass/fail threshold, promotion rule or alpha claim is declared here. Reader cohorts, "
                + "development examples, legacy and retrospective content never pool with the clean "
                + "prospective cohort.");
        sb.AppendLine();
        sb.AppendLine(boundary is null
            ? "Prospective boundary: NOT ESTABLISHED — no clean prospective cohort can exist yet."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Prospective boundary: {boundary.FirstProspectiveCaptureAsOfUtc:yyyy-MM-dd'T'HH:mm:ss'Z'} "
                    + $"(batch `{boundary.EstablishedByBatchId:D}`)."));
        if (examples is null)
        {
            sb.AppendLine();
            sb.AppendLine(
                "**Development declarations UNAVAILABLE** — the clean prospective table is suppressed "
                    + "(fail closed): without the declarations a development example could leak into it.");
        }

        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Assessments: {rows.Count} · companies: "
                + $"{rows.Select(r => r.Assessment.CompanyId).Distinct().Count()} · as-of dates: "
                + $"{rows.Select(r => DateOnly.FromDateTime(r.Assessment.AssessmentCutoffUtc.UtcDateTime)).Distinct().Count()}"));
        sb.AppendLine();

        // Every exclusion reason, counted.
        var exclusionCounts = rows
            .Where(r => r.Table == NewsRiskEvaluationTable.Excluded)
            .SelectMany(r => r.ExclusionReasons)
            .GroupBy(r => r, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (exclusionCounts.Count > 0)
        {
            sb.AppendLine("## Exclusions");
            sb.AppendLine();
            foreach (var group in exclusionCounts)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture, $"- {group.Key}: {group.Count()}"));
            }

            sb.AppendLine();
        }

        // Per reader cohort, never pooled.
        foreach (var cohort in rows
            .GroupBy(r => r.Assessment.CohortKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var reader = cohort.First().Assessment;
            sb.AppendLine($"## Cohort `{cohort.Key}`");
            sb.AppendLine();
            sb.AppendLine($"Reader(s): {string.Join(
                ", ",
                cohort.Select(r => r.Assessment.ReaderName).Distinct(StringComparer.OrdinalIgnoreCase))} "
                + $"· provider `{reader.Provider}` · model `{reader.ModelId}`");
            sb.AppendLine();

            if (examples is not null)
            {
                RenderCohortTable(
                    sb,
                    "Clean prospective",
                    cohort.Where(r => r.Table == NewsRiskEvaluationTable.CleanProspective).ToList(),
                    renderAssociations: true);
            }

            RenderCohortTable(
                sb,
                "Known development examples (never validation evidence)",
                cohort.Where(r => r.Table == NewsRiskEvaluationTable.KnownDevelopmentExample).ToList(),
                renderAssociations: false);
            RenderCohortTable(
                sb,
                "LegacyHeadlineOnly (development)",
                cohort.Where(r => r.Table == NewsRiskEvaluationTable.LegacyHeadlineOnly).ToList(),
                renderAssociations: false);
            RenderCohortTable(
                sb,
                "RetrospectiveUrlFetch (development)",
                cohort.Where(r => r.Table == NewsRiskEvaluationTable.RetrospectiveUrlFetch).ToList(),
                renderAssociations: false);
        }

        return sb.ToString();
    }

    private static void RenderCohortTable(
        StringBuilder sb, string title, IReadOnlyList<NewsRiskEvaluationRow> rows, bool renderAssociations)
    {
        sb.AppendLine($"### {title}");
        sb.AppendLine();
        if (rows.Count == 0)
        {
            sb.AppendLine("(no rows)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Company | As-of (cutoff) | Status | RiskScore | Fwd 21d | Max adverse 21d | Selected by |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in rows
            .OrderBy(r => r.Assessment.AssessmentCutoffUtc)
            .ThenBy(r => r.Assessment.CompanyId))
        {
            var a = row.Assessment;
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {EscapePipes(a.CompanyName)} ({EscapePipes(a.Ticker ?? "—")}) "
                    + $"| {a.AssessmentCutoffUtc:yyyy-MM-dd} | {a.Status} "
                    + $"| {(a.RiskScore is { } s ? s.ToString(CultureInfo.InvariantCulture) : "—")} "
                    + $"| {FormatPct(row.ForwardReturn21d)} | {FormatPct(row.MaxAdverseMove21d)} "
                    + $"| {EscapePipes(string.Join("; ", a.Selections.Select(sel => $"{sel.StrategyName} #{sel.Rank}")))} |"));
        }

        sb.AppendLine();

        if (!renderAssociations)
        {
            return;
        }

        // Per-date associations between RiskScore and adverse move, ThesisChallenged rows only (the rows
        // that carry a score) — plus tie/constant-predictor frequency. Descriptive, never a claim.
        var flagged = rows
            .Where(r => r.Assessment.Status == NewsRiskAssessmentStatus.ThesisChallenged
                && r.Assessment.RiskScore is not null
                && r.MaxAdverseMove21d is not null)
            .ToList();
        var nonFlagged = rows
            .Where(r => r.Assessment.Status == NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText)
            .ToList();

        sb.AppendLine("#### Flagged vs non-flagged (descriptive)");
        sb.AppendLine();
        sb.AppendLine(DescriptiveLine("Flagged (ThesisChallenged)", flagged));
        sb.AppendLine(DescriptiveLine("Non-flagged (NoRiskFoundInSuppliedText)", nonFlagged));
        sb.AppendLine();

        var constantDates = 0;
        var definedDates = 0;
        sb.AppendLine("#### Per-date RiskScore ↔ max-adverse-move association (Spearman ρ, flagged rows)");
        sb.AppendLine();
        foreach (var date in flagged
            .GroupBy(r => DateOnly.FromDateTime(r.Assessment.AssessmentCutoffUtc.UtcDateTime))
            .OrderBy(g => g.Key))
        {
            var scores = date.Select(r => (double)r.Assessment.RiskScore!.Value).ToList();
            // Adverse moves are ≤ 0; the association is computed against their MAGNITUDE so a positive ρ
            // reads as "higher risk score, larger adverse move".
            var adverse = date.Select(r => -r.MaxAdverseMove21d!.Value).ToList();
            var rho = RankCorrelation.ComputeRho(scores, adverse);
            if (rho.IsDefined)
            {
                definedDates++;
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"- {date.Key:yyyy-MM-dd}: n={rho.ObservationCount}, ρ={rho.Rho:0.000}"));
            }
            else
            {
                if (rho.Reason is RankCorrelationUndefinedReason.ConstantScores)
                {
                    constantDates++;
                }

                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"- {date.Key:yyyy-MM-dd}: n={rho.ObservationCount}, ρ undefined ({rho.Reason})"));
            }
        }

        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Constant-predictor dates: {constantDates}; dates with a defined ρ: {definedDates}."));
        sb.AppendLine();
    }

    private static string DescriptiveLine(string label, IReadOnlyList<NewsRiskEvaluationRow> rows)
    {
        if (rows.Count == 0)
        {
            return $"- {label}: (no rows)";
        }

        var returns = rows.Where(r => r.ForwardReturn21d is not null)
            .Select(r => r.ForwardReturn21d!.Value).ToList();
        var adverse = rows.Where(r => r.MaxAdverseMove21d is not null)
            .Select(r => r.MaxAdverseMove21d!.Value).ToList();
        var returnsPart = returns.Count > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $", mean fwd {returns.Average():+0.00%;-0.00%}, worst fwd {returns.Min():+0.00%;-0.00%}")
            : string.Empty;
        var adversePart = adverse.Count > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $", mean max-adverse {adverse.Average():+0.00%;-0.00%}, worst max-adverse {adverse.Min():+0.00%;-0.00%}")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture, $"- {label}: n={rows.Count}{returnsPart}{adversePart}");
    }

    private static string RenderCsv(IReadOnlyList<NewsRiskEvaluationRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "assessmentId,runId,cohortKey,readerName,provider,model,companyId,companyName,ticker,"
                + "selectionAsOfUtc,assessmentCutoffUtc,captureModes,coverageComplete,status,riskScore,"
                + "categories,table,exclusionReasons,entryDate,forwardReturn21d,maxAdverseMove21d,selectedBy");
        foreach (var row in rows
            .OrderBy(r => r.Assessment.AssessmentCutoffUtc)
            .ThenBy(r => r.Assessment.AssessmentId))
        {
            var a = row.Assessment;
            var captureModes = string.Join(
                "|", a.Observations.Select(o => o.CaptureMode.ToString()).Distinct());
            sb.AppendLine(string.Join(
                ",",
                CsvField.Escape(a.AssessmentId.ToString("D")),
                CsvField.Escape(a.RunId.ToString("D")),
                CsvField.Escape(a.CohortKey),
                CsvField.Escape(a.ReaderName),
                CsvField.Escape(a.Provider),
                CsvField.Escape(a.ModelId),
                CsvField.Escape(a.CompanyId.ToString("D")),
                CsvField.Escape(a.CompanyName),
                CsvField.Escape(a.Ticker),
                CsvField.Escape(a.SelectionAsOfUtc.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                CsvField.Escape(a.AssessmentCutoffUtc.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                CsvField.Escape(captureModes),
                CsvField.Escape(a.CoverageComplete ? "true" : "false"),
                CsvField.Escape(a.Status.ToString()),
                CsvField.Escape(a.RiskScore?.ToString(CultureInfo.InvariantCulture)),
                CsvField.Escape(string.Join("|", a.Categories)),
                CsvField.Escape(row.Table.ToString()),
                CsvField.Escape(string.Join("|", row.ExclusionReasons)),
                CsvField.Escape(row.EntryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                CsvField.Escape(row.ForwardReturn21d?.ToString("0.######", CultureInfo.InvariantCulture)),
                CsvField.Escape(row.MaxAdverseMove21d?.ToString("0.######", CultureInfo.InvariantCulture)),
                CsvField.Escape(string.Join(
                    "|", a.Selections.Select(s => $"{s.StrategyName}#{s.Rank}")))));
        }

        return sb.ToString();
    }

    private static string EscapePipes(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string FormatPct(double? value) =>
        value is { } v ? v.ToString("+0.00%;-0.00%", CultureInfo.InvariantCulture) : "—";
}
