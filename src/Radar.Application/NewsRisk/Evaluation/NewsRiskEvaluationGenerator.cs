using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.News;
using Radar.Application.Prices;

namespace Radar.Application.NewsRisk.Evaluation;

/// <summary>
/// Which table one evaluator row belongs to — the cohorts NEVER pool (spec 179 §8/§9). Spec 182 §4 split
/// the former "clean prospective" table into presence and absence claims: nothing named "clean" may admit
/// caveated rows, and completeness gates only the ABSENCE claim.
/// </summary>
public enum NewsRiskEvaluationTable
{
    /// <summary>
    /// A validated <c>ThesisChallenged</c> row carrying a RiskScore — admitted at ANY completeness
    /// (post-boundary, prospective, completed-validated, non-development, resolved forward window), and
    /// segmented by the three completeness dimensions so degraded and best-state rows never silently pool.
    /// </summary>
    PresenceClaim = 0,

    /// <summary>
    /// A <c>NoRiskFoundInSuppliedText</c> row — admitted ONLY when all three completeness dimensions are
    /// at their best state (plus the same other gates), because only there does "found nothing" carry
    /// evidential weight.
    /// </summary>
    AbsenceClaim,

    /// <summary>A declared development example (EOSE/CASS/MSEX…): visible, never validation evidence.</summary>
    KnownDevelopmentExample,

    /// <summary>Input included migrated headline-only observations — development table.</summary>
    LegacyHeadlineOnly,

    /// <summary>Input included retrospectively fetched content — development table, never point-in-time.</summary>
    RetrospectiveUrlFetch,

    /// <summary>Excluded from every analytic table, with named reasons.</summary>
    Excluded,
}

/// <summary>
/// One frozen company/run/reader assessment joined (read-only) to its forward outcome. Both forward returns
/// — RAW and EXCESS-vs-benchmark-universe-v1 (spec 183 §3) — are DESCRIPTIVE fields: spec 179 declares no
/// threshold or alpha claim, and the RiskScore association keeps its raw max-adverse basis.
/// </summary>
public sealed record NewsRiskEvaluationRow(
    NewsRiskAssessmentRecord Assessment,
    NewsRiskEvaluationTable Table,
    IReadOnlyList<string> ExclusionReasons,
    DateOnly? EntryDate,
    double? ForwardReturn21d,
    double? MaxAdverseMove21d)
{
    /// <summary>The excess 21-day forward return vs the frozen benchmark universe — descriptive only.</summary>
    public double? ExcessForwardReturn21d { get; init; }

    /// <summary>Why the excess is null (<c>None</c> when defined) — the named, never-silent exclusion.</summary>
    public BenchmarkExcessUnavailableReason ExcessUnavailableReason { get; init; } =
        BenchmarkExcessUnavailableReason.BenchmarkUnavailable;
}

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
/// number is never invented for them, and the non-flagged line draws ONLY from admitted absence-claim
/// rows (a degraded-completeness "found nothing" was never a claim, so it appears in no such accounting).</item>
/// <item><b>Reader cohorts never pool</b>: every table and every association is per cohort key. The three
/// spec-182 completeness dimensions join that segmentation — every aggregate line over presence rows states
/// the dimension combination it covers.</item>
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
    private readonly IUniverseBenchmarkProvider _benchmarkProvider;
    private readonly ILogger<NewsRiskEvaluationGenerator> _logger;

    public NewsRiskEvaluationGenerator(
        INewsRiskAssessmentStore assessmentStore,
        INewsRiskDevelopmentExampleSource developmentExamples,
        INewsProspectiveBoundaryReader boundaryReader,
        IPriceHistoryStore priceStore,
        INewsRiskArtifactStore artifactStore,
        // Spec 183: the SAME central frozen-universe benchmark the leaderboard consumes (one computation per
        // (universeVersion, D, horizon, tolerance), shared) — required, never optional-nullable, so a wiring
        // mistake fails resolution instead of silently rendering every excess as unavailable.
        IUniverseBenchmarkProvider benchmarkProvider,
        ILogger<NewsRiskEvaluationGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(assessmentStore);
        ArgumentNullException.ThrowIfNull(developmentExamples);
        ArgumentNullException.ThrowIfNull(boundaryReader);
        ArgumentNullException.ThrowIfNull(priceStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(benchmarkProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _assessmentStore = assessmentStore;
        _developmentExamples = developmentExamples;
        _boundaryReader = boundaryReader;
        _priceStore = priceStore;
        _artifactStore = artifactStore;
        _benchmarkProvider = benchmarkProvider;
        _logger = logger;
    }

    public async Task GenerateAsync(CancellationToken ct)
    {
        try
        {
            var assessments = await _assessmentStore.GetAllAsync(ct).ConfigureAwait(false);
            var examples = await _developmentExamples.GetAllAsync(ct).ConfigureAwait(false);
            var boundary = await _boundaryReader.ReadBoundaryAsync(ct).ConfigureAwait(false);
            var benchmark = await _benchmarkProvider.GetAsync(ct).ConfigureAwait(false);

            var rows = new List<NewsRiskEvaluationRow>(assessments.Count);
            foreach (var assessment in assessments)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(await BuildRowAsync(assessment, examples, boundary, benchmark, ct)
                    .ConfigureAwait(false));
            }

            var markdown = RenderMarkdown(rows, examples, boundary);
            var csv = RenderCsv(rows);
            await _artifactStore.WriteEvaluationAsync(markdown, csv, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "News-risk evaluation written: {Rows} row(s), {Presence} presence-claim, "
                    + "{Absence} absence-claim, {Development} development, {Excluded} excluded.",
                rows.Count,
                rows.Count(r => r.Table == NewsRiskEvaluationTable.PresenceClaim),
                rows.Count(r => r.Table == NewsRiskEvaluationTable.AbsenceClaim),
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
        UniverseBenchmark? benchmark,
        CancellationToken ct)
    {
        // Outcome join first (computed for development rows too — their tables display outcomes; they just
        // never pool with the clean cohort).
        DateOnly? entryDate = null;
        double? forwardReturn = null;
        double? maxAdverse = null;
        double? excessReturn = null;
        var excessReason = BenchmarkExcessUnavailableReason.BenchmarkUnavailable;
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

                    // Spec 183 §3: the DESCRIPTIVE excess against the frozen universe, through the SAME
                    // central computation the leaderboard consumes. Unavailability stays a named reason —
                    // never silently rendered as the raw value.
                    var excess = benchmark?.TryExcess(
                        assessment.CompanyId, forward.Value, asOf, HorizonDays, ExitToleranceDays);
                    if (excess is { IsDefined: true })
                    {
                        excessReturn = excess.Excess;
                        excessReason = BenchmarkExcessUnavailableReason.None;
                    }
                    else if (excess is not null)
                    {
                        excessReason = excess.Reason;
                    }
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
            // The prospective-claim gates (spec 179 §9, amended by spec 182 §4), every one named on
            // failure. Completeness is deliberately NOT a gate here — it gates only the absence claim.
            if (boundary is null)
            {
                reasons.Add("no-prospective-boundary");
            }
            else if (assessment.AssessmentCutoffUtc < boundary.FirstProspectiveCaptureAsOfUtc)
            {
                reasons.Add("before-prospective-boundary");
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

            if (assessment.Status == NewsRiskAssessmentStatus.ThesisChallenged)
            {
                // Presence claim: admitted at ANY completeness — the dimensions segment, never exclude.
                if (assessment.RiskScore is null)
                {
                    reasons.Add("presence-claim-missing-risk-score");
                }

                table = reasons.Count == 0
                    ? NewsRiskEvaluationTable.PresenceClaim
                    : NewsRiskEvaluationTable.Excluded;
            }
            else if (assessment.Status == NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText)
            {
                // Absence claim: "found nothing" carries evidential weight ONLY over provably-complete
                // input, so any degraded dimension is a named exclusion. v1 records (no dimension fields)
                // deserialize at every dimension's degraded zero default and can therefore never enter.
                var degraded = DegradedDimensionTokens(assessment);
                if (degraded.Count > 0)
                {
                    reasons.Add(
                        "absence-claim-requires-complete-coverage: " + string.Join(",", degraded));
                }

                table = reasons.Count == 0
                    ? NewsRiskEvaluationTable.AbsenceClaim
                    : NewsRiskEvaluationTable.Excluded;
            }
            else
            {
                // Every other status already carries the assessment-not-completed-validated reason.
                table = NewsRiskEvaluationTable.Excluded;
            }
        }

        return new NewsRiskEvaluationRow(assessment, table, reasons, entryDate, forwardReturn, maxAdverse)
        {
            ExcessForwardReturn21d = excessReturn,
            ExcessUnavailableReason = excessReason,
        };
    }

    /// <summary>Stable machine token for the CSV's excess-basis column.</summary>
    private static string ExcessBasisToken(NewsRiskEvaluationRow row) => row.ExcessUnavailableReason switch
    {
        BenchmarkExcessUnavailableReason.None => "excess-vs-benchmark-universe-v1",
        BenchmarkExcessUnavailableReason.NotInBenchmarkUniverse => "not-in-benchmark-universe",
        _ => "benchmark-unavailable",
    };

    /// <summary>The degraded dimensions as machine-readable exclusion tokens — empty at best-state.</summary>
    private static IReadOnlyList<string> DegradedDimensionTokens(NewsRiskAssessmentRecord assessment)
    {
        var tokens = new List<string>();
        if (assessment.ArchiveCapture != NewsRiskArchiveCapture.Proven)
        {
            tokens.Add("archiveCapture=" + assessment.ArchiveCapture);
        }

        if (assessment.SearchEnumeration != NewsRiskSearchEnumeration.Complete)
        {
            tokens.Add("searchEnumeration=" + assessment.SearchEnumeration);
        }

        if (assessment.AssessmentBundle != NewsRiskAssessmentBundle.Complete)
        {
            tokens.Add("bundle=" + assessment.AssessmentBundle);
        }

        return tokens;
    }

    /// <summary>
    /// The full dimension combination one aggregate line covers (spec 182 §4: presence-claim aggregates
    /// never silently pool degraded and best-state rows).
    /// </summary>
    private static string DimensionCombination(NewsRiskAssessmentRecord assessment) =>
        $"archiveCapture={assessment.ArchiveCapture}, searchEnumeration={assessment.SearchEnumeration}, "
            + $"assessmentBundle={assessment.AssessmentBundle}";

    /// <summary>The compact per-row rendering of all three dimensions, archive/search/bundle order.</summary>
    private static string DimensionsCell(NewsRiskAssessmentRecord assessment) =>
        $"{assessment.ArchiveCapture}/{assessment.SearchEnumeration}/{assessment.AssessmentBundle}";

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
                + "development examples, legacy and retrospective content never pool with the prospective "
                + "presence/absence-claim cohorts; presence claims are admitted at any completeness and "
                + "segmented by the three completeness dimensions, while absence claims require best-state "
                + "dimensions on every one.");
        sb.AppendLine();
        sb.AppendLine(
            "Forward returns are DESCRIPTIVE, in both forms (spec 183): the raw 21-day return and the "
                + "excess 21-day return vs benchmark-universe-v1 (raw minus the equal-weight mean forward "
                + "return of the other resolved frozen-universe members, self-excluded). Excess values on "
                + "as-of dates before the universe freeze are additionally RETROSPECTIVE — the frozen "
                + "members were selected after those dates and their prices backfilled. The RiskScore "
                + "association keeps its RAW max-adverse-move basis. Nothing here is claim-bearing.");
        sb.AppendLine();
        sb.AppendLine(boundary is null
            ? "Prospective boundary: NOT ESTABLISHED — no prospective presence/absence-claim cohort can exist yet."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Prospective boundary: {boundary.FirstProspectiveCaptureAsOfUtc:yyyy-MM-dd'T'HH:mm:ss'Z'} "
                    + $"(batch `{boundary.EstablishedByBatchId:D}`)."));
        if (examples is null)
        {
            sb.AppendLine();
            sb.AppendLine(
                "**Development declarations UNAVAILABLE** — the presence/absence-claim tables are "
                    + "suppressed (fail closed): without the declarations a development example could "
                    + "leak into them.");
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
                var presence = cohort
                    .Where(r => r.Table == NewsRiskEvaluationTable.PresenceClaim).ToList();
                var absence = cohort
                    .Where(r => r.Table == NewsRiskEvaluationTable.AbsenceClaim).ToList();
                RenderCohortTable(
                    sb,
                    "Presence claims (validated risks, any completeness — dimension-segmented)",
                    presence);
                RenderClaimAssociations(sb, presence, absence);
                RenderCohortTable(
                    sb,
                    "Absence claims (nothing found in supplied text — complete coverage only)",
                    absence);
            }

            RenderCohortTable(
                sb,
                "Known development examples (never validation evidence)",
                cohort.Where(r => r.Table == NewsRiskEvaluationTable.KnownDevelopmentExample).ToList());
            RenderCohortTable(
                sb,
                "LegacyHeadlineOnly (development)",
                cohort.Where(r => r.Table == NewsRiskEvaluationTable.LegacyHeadlineOnly).ToList());
            RenderCohortTable(
                sb,
                "RetrospectiveUrlFetch (development)",
                cohort.Where(r => r.Table == NewsRiskEvaluationTable.RetrospectiveUrlFetch).ToList());
        }

        return sb.ToString();
    }

    private static void RenderCohortTable(
        StringBuilder sb, string title, IReadOnlyList<NewsRiskEvaluationRow> rows)
    {
        sb.AppendLine($"### {title}");
        sb.AppendLine();
        if (rows.Count == 0)
        {
            sb.AppendLine("(no rows)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(
            "| Company | As-of (cutoff) | Status | RiskScore | Completeness (archive/search/bundle) "
                + "| Fwd 21d (raw, descriptive) | Excess fwd 21d vs universe-v1 (descriptive) "
                + "| Max adverse 21d (raw) | Selected by |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
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
                    + $"| {DimensionsCell(a)} "
                    + $"| {FormatPct(row.ForwardReturn21d)} | {ExcessCell(row)} "
                    + $"| {FormatPct(row.MaxAdverseMove21d)} "
                    + $"| {EscapePipes(string.Join("; ", a.Selections.Select(sel => $"{sel.StrategyName} #{sel.Rank}")))} |"));
        }

        sb.AppendLine();
    }

    /// <summary>
    /// The descriptive split and per-date associations over the ADMITTED claim rows (spec 182 §4):
    /// flagged aggregates are broken down per dimension combination (degraded and best-state presence rows
    /// never silently pool into one number — every aggregate line states the combination it covers), and
    /// the non-flagged line draws ONLY from absence-claim rows, which required best-state dimensions to be
    /// admitted at all.
    /// </summary>
    private static void RenderClaimAssociations(
        StringBuilder sb,
        IReadOnlyList<NewsRiskEvaluationRow> presence,
        IReadOnlyList<NewsRiskEvaluationRow> absence)
    {
        var flagged = presence
            .Where(r => r.Assessment.RiskScore is not null && r.MaxAdverseMove21d is not null)
            .ToList();

        sb.AppendLine("#### Flagged vs non-flagged (descriptive)");
        sb.AppendLine();
        if (flagged.Count == 0)
        {
            sb.AppendLine("- Flagged (PresenceClaim): (no rows)");
        }
        else
        {
            foreach (var combination in flagged
                .GroupBy(r => DimensionCombination(r.Assessment), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine(DescriptiveLine(
                    $"Flagged (PresenceClaim) [{combination.Key}]", combination.ToList()));
            }
        }

        sb.AppendLine(DescriptiveLine(
            "Non-flagged (AbsenceClaim — complete coverage only)", absence));
        sb.AppendLine();

        var constantDates = 0;
        var definedDates = 0;
        sb.AppendLine(
            "#### Per-date RiskScore ↔ max-adverse-move association "
                + "(Spearman ρ, flagged rows, RAW max adverse move, per dimension combination)");
        sb.AppendLine();
        foreach (var combination in flagged
            .GroupBy(r => DimensionCombination(r.Assessment), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var date in combination
                .GroupBy(r => DateOnly.FromDateTime(r.Assessment.AssessmentCutoffUtc.UtcDateTime))
                .OrderBy(g => g.Key))
            {
                var scores = date.Select(r => (double)r.Assessment.RiskScore!.Value).ToList();
                // Adverse moves are ≤ 0; the association is computed against their MAGNITUDE so a positive
                // ρ reads as "higher risk score, larger adverse move".
                var adverse = date.Select(r => -r.MaxAdverseMove21d!.Value).ToList();
                var rho = RankCorrelation.ComputeRho(scores, adverse);
                if (rho.IsDefined)
                {
                    definedDates++;
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"- {date.Key:yyyy-MM-dd} [{combination.Key}]: n={rho.ObservationCount}, ρ={rho.Rho:0.000}"));
                }
                else
                {
                    if (rho.Reason is RankCorrelationUndefinedReason.ConstantScores)
                    {
                        constantDates++;
                    }

                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"- {date.Key:yyyy-MM-dd} [{combination.Key}]: n={rho.ObservationCount}, "
                            + $"ρ undefined ({rho.Reason})"));
                }
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
                + "selectionAsOfUtc,assessmentCutoffUtc,captureModes,archiveCapture,searchEnumeration,"
                + "assessmentBundle,status,riskScore,"
                + "categories,table,exclusionReasons,entryDate,rawForwardReturn21d,"
                + "excessForwardReturn21d,excessForwardReturn21dBasis,maxAdverseMove21dRaw,selectedBy");
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
                CsvField.Escape(a.ArchiveCapture.ToString()),
                CsvField.Escape(a.SearchEnumeration.ToString()),
                CsvField.Escape(a.AssessmentBundle.ToString()),
                CsvField.Escape(a.Status.ToString()),
                CsvField.Escape(a.RiskScore?.ToString(CultureInfo.InvariantCulture)),
                CsvField.Escape(string.Join("|", a.Categories)),
                CsvField.Escape(row.Table.ToString()),
                CsvField.Escape(string.Join("|", row.ExclusionReasons)),
                CsvField.Escape(row.EntryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                CsvField.Escape(row.ForwardReturn21d?.ToString("0.######", CultureInfo.InvariantCulture)),
                CsvField.Escape(row.ExcessForwardReturn21d?.ToString("0.######", CultureInfo.InvariantCulture)),
                // The basis column names what the excess value IS (or why it is absent), so the two return
                // columns can never be read as one series (spec 183: both descriptive, different outcomes).
                CsvField.Escape(row.ForwardReturn21d is null ? string.Empty : ExcessBasisToken(row)),
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

    /// <summary>
    /// The excess cell: the value when defined, otherwise the NAMED unavailability — an excess that could
    /// not be computed must never render like a raw value or a blank (spec 183: no silent fallback).
    /// </summary>
    private static string ExcessCell(NewsRiskEvaluationRow row) =>
        row.ExcessForwardReturn21d is { } v
            ? v.ToString("+0.00%;-0.00%", CultureInfo.InvariantCulture)
            : row.ForwardReturn21d is null
                ? "—"
                : $"— ({ExcessBasisToken(row)})";
}
