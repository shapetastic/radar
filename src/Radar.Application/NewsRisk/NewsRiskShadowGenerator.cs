using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;

namespace Radar.Application.NewsRisk;

/// <summary>
/// The in-process shadow news-risk step (spec 179 §2), invoked by the Worker AFTER <c>IRadarPipeline</c>
/// returns and BEFORE the efficacy step. Consumes the EXACT spec-176 structured section instances handed
/// through <c>RadarPipelineResult</c> — never parsed Markdown, never a reopened/re-ranked score store.
/// </summary>
public interface INewsRiskShadowGenerator
{
    /// <summary>
    /// Runs the shadow read for the completed run <paramref name="runId"/> over the exact
    /// <paramref name="strategySections"/> the report builder produced. Never throws for its own failures
    /// (a shadow failure writes the NAMED failed artifact and never rolls back or relabels the
    /// already-durable Radar run); caller cancellation propagates. The optional
    /// <paramref name="judgment"/> (spec 185 §5) carries the SAME judgment records the stage-2 pass just
    /// persisted, so the live artifact embeds the per-company judgment sections and marker states beside
    /// the single-call read — side by side, cohorts never pooled, no merged verdict.
    /// </summary>
    Task GenerateAsync(
        Guid? runId,
        IReadOnlyList<StrategyReportSection>? strategySections,
        CancellationToken ct,
        NewsJudgmentRunResult? judgment = null);
}

/// <summary>
/// Orchestrates one shadow pass (spec 179): frozen candidate selection (§3) → point-in-time input bundles
/// (§4) → one analyzer pass per configured reader (§5) → mechanical validation (§6) → durable per-attempt
/// persistence with the §6 cache → the fail-closed live artifact (§7).
/// <para>
/// AD-14 boundary: this type has NO price dependency of any kind (asserted structurally by the news-risk
/// architecture guard test) — only the separate read-only §9 evaluator may touch price. It also holds no
/// score repository/file-store seam: the ONLY row source is the handed-in section instances.
/// </para>
/// </summary>
public sealed class NewsRiskShadowGenerator : INewsRiskShadowGenerator
{
    /// <summary>How many recent run records are scanned to resolve the durable record for this run id.</summary>
    private const int RunLookupWindow = 50;

    private readonly IPipelineRunStore _runStore;
    private readonly INewsObservationArchive _observationArchive;
    private readonly INewsObservationBatchReader _batchReader;
    private readonly NewsRiskReaderSet _readers;
    private readonly INewsRiskAssessmentStore _assessmentStore;
    private readonly INewsRiskArtifactStore _artifactStore;
    private readonly NewsRiskShadowOptions _options;
    private readonly INewsArticleContentReader? _contentReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NewsRiskShadowGenerator> _logger;

    public NewsRiskShadowGenerator(
        IPipelineRunStore runStore,
        INewsObservationArchive observationArchive,
        INewsObservationBatchReader batchReader,
        NewsRiskReaderSet readers,
        INewsRiskAssessmentStore assessmentStore,
        INewsRiskArtifactStore artifactStore,
        NewsRiskShadowOptions options,
        TimeProvider timeProvider,
        ILogger<NewsRiskShadowGenerator> logger,
        INewsArticleContentReader? contentReader = null)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(observationArchive);
        ArgumentNullException.ThrowIfNull(batchReader);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(assessmentStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (readers.Readers.Count == 0)
        {
            throw new ArgumentException(
                "NewsRiskReaderSet must resolve at least one reader; the composition root registers the "
                    + "shadow step only when one (ambient or configured) exists.",
                nameof(readers));
        }

        _runStore = runStore;
        _observationArchive = observationArchive;
        _batchReader = batchReader;
        _readers = readers;
        _assessmentStore = assessmentStore;
        _artifactStore = artifactStore;
        _options = options;
        _contentReader = contentReader;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task GenerateAsync(
        Guid? runId,
        IReadOnlyList<StrategyReportSection>? strategySections,
        CancellationToken ct,
        NewsJudgmentRunResult? judgment = null)
    {
        var fallbackDateToken = _timeProvider.GetUtcNow().UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            await GenerateCoreAsync(runId, strategySections, judgment, fallbackDateToken, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A shadow failure must never abort or relabel the already-durable Radar run (spec 179 §7):
            // write the named failed artifact (itself best-effort) and return.
            _logger.LogError(ex, "News-risk shadow read failed; writing the named failed artifact.");
            await _artifactStore
                .WriteFailedAsync(fallbackDateToken, $"{ex.GetType().Name}: {ex.Message}", ct)
                .ConfigureAwait(false);
        }
    }

    private async Task GenerateCoreAsync(
        Guid? runId,
        IReadOnlyList<StrategyReportSection>? strategySections,
        NewsJudgmentRunResult? judgment,
        string fallbackDateToken,
        CancellationToken ct)
    {
        var readerLabels = _readers.Readers
            .Select(r => $"{r.Identity.Name} ({r.Identity.Provider}:{r.Identity.ModelId})")
            .ToList();

        if (runId is not { } id)
        {
            await _artifactStore
                .WriteFailedAsync(
                    fallbackDateToken,
                    "RunIdUnavailable: the pipeline result carried no durable run id.",
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var runRecord = await FindRunRecordAsync(id, ct).ConfigureAwait(false);
        if (runRecord is null)
        {
            await _artifactStore
                .WriteFailedAsync(
                    fallbackDateToken,
                    $"RunRecordNotFound: no durable run record with id {id:D} was readable.",
                    ct)
                .ConfigureAwait(false);
            return;
        }

        // The selection cutoff is the EXACT completed run's as-of instant (spec 179 §3): a candidate can
        // never be selected from a report row belonging to another run.
        var selectionAsOfUtc = runRecord.CreatedAtUtc;
        var asOfDateToken = selectionAsOfUtc.UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var candidates = strategySections is { Count: > 0 }
            ? NewsRiskCandidateSelector.Select(strategySections, _options.MaxCompaniesPerRun)
            : [];

        if (candidates.Count == 0)
        {
            // Named diagnostic, never invented rows (spec 179 §2): a single-strategy run builds no
            // sections, and a run whose Research sections held no evidence-linked rows selects nothing.
            var diagnosticDocument = new NewsRiskLiveDocument(
                SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
                RunId: id,
                SelectionAsOfUtc: selectionAsOfUtc,
                Caveat: NewsRiskLiveDocument.LiveCaveat,
                Readers: readerLabels,
                Diagnostic: NewsRiskLiveDocument.NoLiveStrategySections,
                Companies: [],
                GeneratedAtUtc: _timeProvider.GetUtcNow());
            await _artifactStore
                .WriteLiveAsync(
                    asOfDateToken,
                    NewsRiskLiveArtifactRenderer.RenderMarkdown(diagnosticDocument),
                    diagnosticDocument,
                    ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "News-risk shadow read: no live strategy sections for run {RunId}; wrote the "
                    + "NoLiveStrategySections diagnostic artifact.",
                id);
            return;
        }

        var observations = await _observationArchive.GetAllAsync(ct).ConfigureAwait(false);
        var batch = runRecord.NewsObservationBatchId is { } batchId
            ? await _batchReader.GetBatchAsync(batchId, ct).ConfigureAwait(false)
            : null;

        var companies = new List<NewsRiskLiveCompany>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            companies.Add(await AssessCandidateAsync(
                candidate, id, selectionAsOfUtc, observations, batch, judgment, ct).ConfigureAwait(false));
        }

        var document = new NewsRiskLiveDocument(
            SchemaVersion: NewsRiskLiveDocument.CurrentSchemaVersion,
            RunId: id,
            SelectionAsOfUtc: selectionAsOfUtc,
            Caveat: NewsRiskLiveDocument.LiveCaveat,
            Readers: readerLabels,
            Diagnostic: null,
            Companies: companies,
            GeneratedAtUtc: _timeProvider.GetUtcNow(),
            // Spec 194 §1.2: carried straight through from the judgment run result the Worker attached it
            // to. `null` (the judgment step did not run, or predates the materializer) renders nothing.
            SignalMaterialization: judgment?.SignalMaterialization);

        await _artifactStore
            .WriteLiveAsync(
                asOfDateToken, NewsRiskLiveArtifactRenderer.RenderMarkdown(document), document, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "News-risk shadow read complete for run {RunId}: {Companies} candidate(s) × {Readers} reader(s).",
            id,
            companies.Count,
            _readers.Readers.Count);
    }

    private async Task<NewsRiskLiveCompany> AssessCandidateAsync(
        NewsRiskCandidate candidate,
        Guid runId,
        DateTimeOffset selectionAsOfUtc,
        IReadOnlyList<NewsObservationRecord> observations,
        NewsObservationBatch? batch,
        NewsJudgmentRunResult? judgment,
        CancellationToken ct)
    {
        var coverage = NewsRiskCoverageEvaluator.Evaluate(
            batch, candidate.CompanyId, _options.NewsSearchCollectorName);

        var bundle = NewsRiskInputBundleBuilder.Build(
            candidate.CompanyId,
            observations,
            selectionAsOfUtc,
            _options.LookbackDays,
            _options.MaxArticlesPerCompany,
            _options.MaxFetchedArticlesPerCompany);

        // Live body attachment runs for ANY non-empty bundle (spec 182 §1): a fetched body for a supplied
        // article is more information, and more information never requires completeness.
        var fetchWarnings = new List<string>();
        if (_contentReader is not null && bundle.Articles.Count > 0)
        {
            bundle = await AttachLiveBodiesAsync(bundle, fetchWarnings, ct).ConfigureAwait(false);
        }

        var readerResults = new List<NewsRiskLiveReaderResult>(_readers.Readers.Count);
        foreach (var reader in _readers.Readers)
        {
            ct.ThrowIfCancellationRequested();

            // One reader's runtime failure never blocks another (spec 179 §5): each pass is independently
            // recorded, and the analyzer contract already types provider failures rather than throwing.
            var record = await AssessWithReaderAsync(
                reader, candidate, runId, selectionAsOfUtc, bundle, coverage, ct)
                .ConfigureAwait(false);

            var warnings = new List<string>(fetchWarnings);

            // Any degraded dimension is a stated caveat, NEVER a suppression (spec 182 §3): the assessment
            // stands, and the warning names exactly which dimensions are degraded.
            var degraded = NewsRiskCompletenessDescription.DegradedParts(
                record.ArchiveCapture,
                record.SearchEnumeration,
                record.AssessmentBundle,
                bundle.Articles.Count,
                bundle.QualifyingArticleCount);
            if (degraded.Count > 0)
            {
                // "Known incomplete" only when a dimension states a KNOWN incompleteness; unproven-only
                // degradation reads as "not proven" — never overstated into certainty.
                var caveat = NewsRiskCompletenessDescription.HasKnownIncompleteness(
                    record.SearchEnumeration, record.AssessmentBundle)
                    ? " — supplied text is known incomplete"
                    : " — supplied text completeness is not proven";
                warnings.Add(string.Join("; ", degraded) + caveat);
            }

            if (record.ClaimsDropped > 0)
            {
                warnings.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{record.ClaimsDropped} of {record.ClaimsTotal} claim(s) dropped by validation"));
            }

            if (record.FailureDetail is not null)
            {
                warnings.Add(record.FailureDetail);
            }

            if (record.ReusedFromAssessmentId is { } reused)
            {
                warnings.Add($"reused cached assessment `{reused:D}` (same model/prompt/schema/input)");
            }

            readerResults.Add(new NewsRiskLiveReaderResult(
                ReaderName: record.ReaderName,
                Provider: record.Provider,
                ModelId: record.ModelId,
                AssessmentId: record.AssessmentId,
                Status: record.Status,
                AssessmentCutoffUtc: record.AssessmentCutoffUtc,
                RiskScore: record.RiskScore,
                Categories: record.Categories,
                Claims: record.Claims,
                Rationale: record.Rationale,
                Warnings: warnings));
        }

        return new NewsRiskLiveCompany(
            CompanyId: candidate.CompanyId,
            CompanyName: candidate.CompanyName,
            Ticker: candidate.Ticker,
            Selections: candidate.Selections,
            Articles: bundle.Articles
                .Select(a => new NewsRiskLiveArticle(
                    a.ObservationId,
                    a.Headline,
                    a.Publisher,
                    a.Url,
                    a.CaptureMode,
                    InputKind(a)))
                .ToList(),
            ArchiveCapture: coverage.ArchiveCapture,
            SearchEnumeration: coverage.SearchEnumeration,
            AssessmentBundle: bundle.Completeness,
            QualifyingArticleCount: bundle.QualifyingArticleCount,
            CoverageIssues: coverage.Issues,
            ReaderResults: readerResults,
            Judgments: BuildJudgmentSections(candidate.CompanyId, judgment),
            JudgmentMarker: judgment is null
                ? null
                : NewsJudgmentMarkerReportModel.MarkerCellFor(judgment.Markers, candidate.CompanyId),
            // Spec 195 §2: THIS run's freshly built bundle, deliberately. `AttachLiveBodiesAsync` returns a
            // `bundle with { ... }`, so both counts survive body attachment unchanged, and a reader result
            // reused from the assessment cache never supplies them — an old run's syndication breadth
            // displayed as current would be exactly the stale-provenance failure the spec forbids.
            SyndicatedDuplicateCount: bundle.SyndicatedDuplicateCount,
            SyndicatedDistinctPublisherCount: bundle.SyndicatedDistinctPublisherCount);
    }

    /// <summary>
    /// The spec-185 §5 per-company judgment sections: every (judge × stage-1 cohort) record for this
    /// company, in deterministic (judge, stage-1 cohort) order — each cohort rendered independently, never
    /// pooled, with its stage-1 fact-drop count beside its own finding-drop accounting (the error split).
    /// Null when the judgment step did not run this pass.
    /// </summary>
    private static IReadOnlyList<NewsRiskLiveJudgment>? BuildJudgmentSections(
        Guid companyId, NewsJudgmentRunResult? judgment)
    {
        if (judgment is null)
        {
            return null;
        }

        return judgment.Judgments
            .Where(j => j.CompanyId == companyId)
            .OrderBy(j => j.JudgeName, StringComparer.Ordinal)
            .ThenBy(j => j.Stage1CohortKey, StringComparer.Ordinal)
            .Select(j => new NewsRiskLiveJudgment(
                JudgeName: j.JudgeName,
                Provider: j.Provider,
                ModelId: j.ModelId,
                Stage1CohortKey: j.Stage1CohortKey,
                JudgmentId: j.JudgmentId,
                Status: j.Status,
                BusinessTrajectory: j.BusinessTrajectory,
                ChallengeStrength: j.ChallengeStrength,
                Findings: j.Findings,
                Rationale: j.Rationale,
                FindingsTotal: j.FindingsTotal,
                FindingsAccepted: j.FindingsAccepted,
                FindingsDropped: j.FindingsDropped,
                FindingDropReasons: j.FindingDropReasons,
                Stage1FactsDroppedInWindow: judgment.Stage1FactsDroppedByCohort.TryGetValue(
                    j.Stage1CohortKey, out var stage1Drops) ? stage1Drops : 0,
                ArchiveCapture: j.ArchiveCapture,
                SearchEnumeration: j.SearchEnumeration,
                ObservationSupply: j.ObservationSupply,
                TypingCompleteness: j.TypingCompleteness,
                FamilyBundle: j.FamilyBundle,
                Families: j.Families,
                TrajectoryFactIds: j.TrajectoryFactIds))
            .ToList();
    }

    private async Task<NewsRiskAssessmentRecord> AssessWithReaderAsync(
        NewsRiskReader reader,
        NewsRiskCandidate candidate,
        Guid runId,
        DateTimeOffset selectionAsOfUtc,
        NewsRiskInputBundle bundle,
        NewsRiskCoverageEvaluation coverage,
        CancellationToken ct)
    {
        var identity = reader.Identity;
        var cohortKey = identity.CohortKey;
        NewsRiskAssessmentRecord record;

        // Readers run whenever at least one qualifying article exists (spec 182 §1): completeness is
        // required for ABSENCE claims, never PRESENCE claims, so no dimension blocks the model call. The
        // dimensions are recorded on EVERY persisted attempt instead.
        if (bundle.Articles.Count == 0)
        {
            record = BaseRecord(
                identity, cohortKey, candidate, runId, selectionAsOfUtc, bundle,
                coverage, NewsRiskAssessmentStatus.NoContent);
        }
        else
        {
            var cached = await _assessmentStore
                .FindCompletedAsync(cohortKey, bundle.BundleHash, ct)
                .ConfigureAwait(false);
            if (cached is not null)
            {
                // The cache carries ONLY the raw verdict fields; the completeness dimensions come from
                // BaseRecord and are therefore always the CURRENT run's (spec 182 §3 — a cached verdict
                // replayed under different coverage circumstances never carries a stale derived state).
                record = BaseRecord(
                    identity, cohortKey, candidate, runId, selectionAsOfUtc, bundle,
                    coverage, cached.Status) with
                {
                    RiskScore = cached.RiskScore,
                    Categories = cached.Categories,
                    Claims = cached.Claims,
                    Rationale = cached.Rationale,
                    ClaimsTotal = cached.ClaimsTotal,
                    ClaimsAccepted = cached.ClaimsAccepted,
                    ClaimsDropped = cached.ClaimsDropped,
                    ClaimDropReasons = cached.ClaimDropReasons,
                    RawResponseHash = cached.RawResponseHash,
                    ReusedFromAssessmentId = cached.AssessmentId,
                };
            }
            else
            {
                record = await AnalyzeAsync(
                    reader, cohortKey, candidate, runId, selectionAsOfUtc, bundle,
                    coverage, ct).ConfigureAwait(false);
            }
        }

        await _assessmentStore.WriteAsync(record, ct).ConfigureAwait(false);
        return record;
    }

    private async Task<NewsRiskAssessmentRecord> AnalyzeAsync(
        NewsRiskReader reader,
        string cohortKey,
        NewsRiskCandidate candidate,
        Guid runId,
        DateTimeOffset selectionAsOfUtc,
        NewsRiskInputBundle bundle,
        NewsRiskCoverageEvaluation coverage,
        CancellationToken ct)
    {
        // The model request carries the company name/ticker and the ordered id-labelled text ONLY (spec 179
        // §5): no Radar score, rank or label, no price, no future outcome, no uncited background. Rank stays
        // OUTPUT provenance on the record, never prompt content.
        var request = new NewsRiskAnalysisRequest(candidate.CompanyName, candidate.Ticker, bundle.Articles);

        NewsRiskAnalysisOutcome outcome;
        try
        {
            outcome = await reader.Analyzer.AnalyzeAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Belt-and-braces: the analyzer contract types provider failures, but a throwing implementation
            // must still degrade to a recorded provider-failure attempt so other readers proceed.
            _logger.LogWarning(
                ex,
                "News-risk reader {Reader} threw for company {Company}; recording a provider failure.",
                reader.Identity.Name,
                candidate.CompanyName);
            outcome = new NewsRiskAnalysisOutcome(
                NewsRiskAnalysisFailure.ProviderError, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }

        var record = BaseRecord(
            reader.Identity, cohortKey, candidate, runId, selectionAsOfUtc, bundle,
            coverage, NewsRiskAssessmentStatus.ProviderFailure) with
        {
            RawResponseHash = outcome.RawResponseHash,
        };

        switch (outcome.Failure)
        {
            case NewsRiskAnalysisFailure.ProviderError:
                return record with
                {
                    Status = NewsRiskAssessmentStatus.ProviderFailure,
                    FailureDetail = outcome.FailureDetail,
                };
            case NewsRiskAnalysisFailure.ParseError:
                return record with
                {
                    Status = NewsRiskAssessmentStatus.ParseFailure,
                    FailureDetail = outcome.FailureDetail,
                };
            default:
            {
                var validated = NewsRiskClaimValidator.Validate(outcome.Response!, bundle.Articles);
                return record with
                {
                    Status = validated.Status,
                    RiskScore = validated.RiskScore,
                    Categories = validated.Categories,
                    Claims = validated.Claims,
                    Rationale = validated.Rationale,
                    ClaimsTotal = validated.ClaimsTotal,
                    ClaimsAccepted = validated.ClaimsAccepted,
                    ClaimsDropped = validated.ClaimsDropped,
                    ClaimDropReasons = validated.ClaimDropReasons,
                };
            }
        }
    }

    private NewsRiskAssessmentRecord BaseRecord(
        NewsRiskReaderIdentity identity,
        string cohortKey,
        NewsRiskCandidate candidate,
        Guid runId,
        DateTimeOffset selectionAsOfUtc,
        NewsRiskInputBundle bundle,
        NewsRiskCoverageEvaluation coverage,
        NewsRiskAssessmentStatus status) =>
        new(
            SchemaVersion: NewsRiskAssessmentRecord.CurrentSchemaVersion,
            AssessmentId: NewsRiskAssessmentRecord.IdentityFor(
                cohortKey, bundle.BundleHash, runId, identity.Name),
            RunId: runId,
            SelectionAsOfUtc: selectionAsOfUtc,
            AssessmentCutoffUtc: bundle.AssessmentCutoffUtc,
            CompanyId: candidate.CompanyId,
            CompanyName: candidate.CompanyName,
            Ticker: candidate.Ticker,
            Selections: candidate.Selections,
            ReaderName: identity.Name,
            Provider: identity.Provider,
            ModelId: identity.ModelId,
            PromptVersion: NewsRiskAnalysisContract.PromptVersion,
            ResultSchemaVersion: NewsRiskAnalysisContract.SchemaVersion,
            CohortKey: cohortKey,
            InputBundleHash: bundle.BundleHash,
            Observations: bundle.Articles
                .Select(a => new NewsRiskInputObservationRef(
                    a.ObservationId,
                    a.PayloadHash,
                    DescriptionSupplied: a.DescriptionText is not null,
                    BodySupplied: a.BodyText is not null,
                    BodyContentHash: a.BodyContentHash,
                    BodyRetrievedAtUtc: a.BodyRetrievedAtUtc,
                    BodyExtractorVersion: a.BodyExtractorVersion,
                    BodyRetrievalPolicy: a.BodyRetrievalPolicy,
                    CaptureMode: a.CaptureMode))
                .ToList(),
            ArchiveCapture: coverage.ArchiveCapture,
            SearchEnumeration: coverage.SearchEnumeration,
            AssessmentBundle: bundle.Completeness,
            CoverageIssues: coverage.Issues,
            Status: status,
            RiskScore: null,
            Categories: [],
            Claims: [],
            Rationale: null,
            ClaimsTotal: 0,
            ClaimsAccepted: 0,
            ClaimsDropped: 0,
            ClaimDropReasons: [],
            RawResponseHash: null,
            FailureDetail: null,
            Limits: _options.ToLimitsRecord(),
            ReusedFromAssessmentId: null,
            CreatedAtUtc: _timeProvider.GetUtcNow());

    /// <summary>
    /// Live publisher-body fetch (spec 179 §4) through the spec-177 allowlisted safe reader, only when that
    /// opt-in seam is composed: at most the fetched-article cap, newest first, skipping articles that
    /// already carry a stored archived body. A body retrieved NOW moves the assessment cutoff to the actual
    /// retrieval instant via the bundle recomputation — never backward, never backdated onto the article's
    /// publication/collection time.
    /// </summary>
    private async Task<NewsRiskInputBundle> AttachLiveBodiesAsync(
        NewsRiskInputBundle bundle, List<string> fetchWarnings, CancellationToken ct)
    {
        var articles = bundle.Articles.ToList();
        var attached = articles.Count(a => a.BodyText is not null);
        for (var i = 0; i < articles.Count && attached < _options.MaxFetchedArticlesPerCompany; i++)
        {
            if (articles[i].BodyText is not null)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var fetch = await _contentReader!.FetchAsync(articles[i].Url, ct).ConfigureAwait(false);
            if (fetch is { Outcome: NewsArticleFetchOutcome.Fetched, BodyText: not null })
            {
                articles[i] = articles[i] with
                {
                    BodyText = fetch.BodyText,
                    BodyContentHash = fetch.ContentHash,
                    BodyRetrievedAtUtc = fetch.RetrievedAtUtc,
                    BodyExtractorVersion = fetch.ExtractorVersion,
                    BodyRetrievalPolicy = fetch.RetrievalPolicy,
                };
                attached++;
            }
            else
            {
                fetchWarnings.Add(
                    $"body fetch {fetch.Outcome} for observation {articles[i].ObservationId:D}");
            }
        }

        return bundle with
        {
            Articles = articles,
            AssessmentCutoffUtc = NewsRiskInputBundleBuilder.ComputeCutoff(
                bundle.SelectionAsOfUtc, articles),
            BundleHash = NewsRiskInputBundleBuilder.ComputeBundleHash(articles),
        };
    }

    private static string InputKind(NewsRiskInputArticle article) => (article.DescriptionText, article.BodyText) switch
    {
        (not null, not null) => "headline+description+body",
        (not null, null) => "headline+description",
        (null, not null) => "headline+body",
        _ => "headline",
    };

    private async Task<PipelineRunRecord?> FindRunRecordAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            var recent = await _runStore.ReadRecentAsync(RunLookupWindow, ct).ConfigureAwait(false);
            return recent.FirstOrDefault(r => r.Id == runId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read the run log while resolving run {RunId}.", runId);
            return null;
        }
    }
}
