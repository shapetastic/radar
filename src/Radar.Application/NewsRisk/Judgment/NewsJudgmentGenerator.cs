using Microsoft.Extensions.Logging;

using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The typed outcome of one judgment pass (spec 185): every judgment record produced/reused this run across
/// all (judge × stage-1 cohort) pairs, plus the leaders-marker model derived from the DESIGNATED
/// presentation cohort only. <see cref="Markers"/> is <c>null</c> when the presentation cohort could not be
/// resolved this run — the Worker then skips the report re-render rather than rendering markers from an
/// undesignated cohort.
/// </summary>
public sealed record NewsJudgmentRunResult(
    IReadOnlyList<NewsJudgmentRecord> Judgments,
    NewsJudgmentMarkerReportModel? Markers,
    // Stage-1 fact-drop counts per extractor cohort key (spec 185 §3): the extraction side of the
    // newly-localizable extraction-vs-judgment error split, rendered beside each judgment cohort's own
    // finding-drop counts in the live artifact.
    IReadOnlyDictionary<string, int> Stage1FactsDroppedByCohort);

/// <summary>
/// The in-process stage-2 direction-judge step (spec 185 §5), invoked by the Worker AFTER the typing pass
/// and BEFORE the news-risk shadow (whose live artifact embeds the judgment sections). Read-side and
/// shadow: no score, label, strategy, fingerprint, snapshot field or report RANK changes — the only
/// presentation it touches is the spec-185 §4 semantic-read marker column, and that only through the
/// policy-derived marker model the Worker re-renders with.
/// </summary>
public interface INewsJudgmentGenerator
{
    /// <summary>
    /// Runs one judgment pass over the completed run's candidates and the typing pass's outcome. Never
    /// throws for its own failures (a judgment failure logs and returns <c>null</c>, leaving the honest
    /// <c>? unassessed</c> first render standing); caller cancellation propagates. A <c>null</c>
    /// <paramref name="typing"/> (the typing pass failed or was skipped) returns <c>null</c>: the judge
    /// structurally cannot run without stage 1.
    /// </summary>
    Task<NewsJudgmentRunResult?> GenerateAsync(
        Guid? runId,
        IReadOnlyList<StrategyReportSection>? strategySections,
        NewsTypingRunResult? typing,
        CancellationToken ct);
}

/// <summary>
/// Orchestrates one judgment pass (spec 185): frozen candidate selection (the spec-179 §3 selector REUSED —
/// the same candidates as the single-call read, same cost-budget semantics) → per (stage-1 cohort × judge
/// reader × candidate): deterministic family-input assembly → the completed-judgment cache → at most ONE
/// model call → mechanical validation → durable per-attempt persistence — then the presentation-cohort
/// marker map, derived by <see cref="NewsJudgmentMarkerPolicy"/> (the model never chooses presentation).
/// <para>
/// The judge consumes ONLY the fact layer (spec 185 §1): canonical families with typed content and size
/// metadata. No raw article prose, no headline, no Radar score/rank/label, no price, no prior judgment.
/// Zero families ⇒ an <see cref="NewsJudgmentStatus.InsufficientFacts"/> record with NO model call.
/// Cohorts never pool: each (judge, stage-1 cohort) pair is its own stage-2 cohort, keyed by construction.
/// </para>
/// </summary>
public sealed class NewsJudgmentGenerator : INewsJudgmentGenerator
{
    private readonly INewsObservationBatchReader _batchReader;
    private readonly NewsJudgmentReaderSet _judges;
    private readonly INewsJudgmentStore _store;
    private readonly NewsJudgmentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NewsJudgmentGenerator> _logger;

    public NewsJudgmentGenerator(
        INewsObservationBatchReader batchReader,
        NewsJudgmentReaderSet judges,
        INewsJudgmentStore store,
        NewsJudgmentOptions options,
        TimeProvider timeProvider,
        ILogger<NewsJudgmentGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(batchReader);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (judges.Readers.Count == 0)
        {
            throw new ArgumentException(
                "NewsJudgmentReaderSet must resolve at least one judge; the composition root registers the "
                    + "judgment step only when one (ambient or configured) exists.",
                nameof(judges));
        }

        _batchReader = batchReader;
        _judges = judges;
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<NewsJudgmentRunResult?> GenerateAsync(
        Guid? runId,
        IReadOnlyList<StrategyReportSection>? strategySections,
        NewsTypingRunResult? typing,
        CancellationToken ct)
    {
        try
        {
            return await GenerateCoreAsync(runId, strategySections, typing, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A judgment failure must never abort or relabel the already-durable Radar run. The first
            // render's `? unassessed (judgment-pending)` markers stand — honest ignorance, never silence.
            _logger.LogError(ex, "News-judgment pass failed; the Radar run itself is unaffected.");
            return null;
        }
    }

    private async Task<NewsJudgmentRunResult?> GenerateCoreAsync(
        Guid? runId,
        IReadOnlyList<StrategyReportSection>? strategySections,
        NewsTypingRunResult? typing,
        CancellationToken ct)
    {
        if (typing is null || typing.Cohorts.Count == 0)
        {
            // The judge consumes ONLY the stage-1 fact layer (spec 185 §1). Without it there is nothing to
            // judge; the report's pending markers stand and the reason is logged, never papered over.
            _logger.LogWarning(
                "News-judgment pass skipped: the typing pass produced no consumable stage-1 outcome "
                    + "(failed or produced no cohorts). Leader rows keep their unassessed markers.");
            return null;
        }

        var candidates = strategySections is { Count: > 0 }
            ? NewsRiskCandidateSelector.Select(strategySections, _options.MaxCompaniesPerRun)
            : [];

        var batch = typing.NewsObservationBatchId is { } batchId
            ? await _batchReader.GetBatchAsync(batchId, ct).ConfigureAwait(false)
            : null;

        var judgments = new List<NewsJudgmentRecord>();
        foreach (var cohort in typing.Cohorts)
        {
            foreach (var judge in _judges.Readers)
            {
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var record = await JudgeOneAsync(judge, cohort, candidate, runId, batch, ct)
                        .ConfigureAwait(false);
                    await _store.WriteAsync(record, ct).ConfigureAwait(false);
                    judgments.Add(record);
                }
            }
        }

        var markers = BuildPresentationMarkers(judgments, typing, runId, candidates);

        _logger.LogInformation(
            "News-judgment pass complete: {Candidates} candidate(s) × {Judges} judge(s) × "
                + "{Stage1Cohorts} stage-1 cohort(s) = {Judgments} judgment record(s); presentation "
                + "markers {MarkerState}.",
            candidates.Count,
            _judges.Readers.Count,
            typing.Cohorts.Count,
            judgments.Count,
            markers is null ? "unresolved" : "derived");

        return new NewsJudgmentRunResult(
            judgments,
            markers,
            typing.Cohorts.ToDictionary(
                c => c.Reader.CohortKey, c => c.FactsDroppedInWindow, StringComparer.Ordinal));
    }

    private async Task<NewsJudgmentRecord> JudgeOneAsync(
        NewsJudgmentReader judge,
        NewsTypingCohortRunResult cohort,
        NewsRiskCandidate candidate,
        Guid? runId,
        NewsObservationBatch? batch,
        CancellationToken ct)
    {
        var cohortKey = judge.Identity.CohortKeyFor(cohort.Reader.CohortKey);
        var bundle = NewsJudgmentInputBuilder.Build(
            candidate.CompanyId, cohort.Families, cohort.FactsById, _options.MaxFamiliesPerJudgment);
        var coverage = NewsRiskCoverageEvaluator.Evaluate(
            batch, candidate.CompanyId, _options.NewsSearchCollectorName);

        // A company with no in-window observations has no completeness entry: vacuously Complete over zero
        // observations (it also has zero facts, so the judgment below is InsufficientFacts regardless).
        var typingCompleteness = cohort.TypingCompletenessByCompany.TryGetValue(
            candidate.CompanyId, out var computed)
            ? computed
            : NewsTypingCompleteness.Complete;

        var baseRecord = BaseRecord(
            judge.Identity, cohortKey, cohort, candidate, runId, bundle, coverage, typingCompleteness);

        if (bundle.Families.Count == 0)
        {
            // Zero canonical families ⇒ a recorded InsufficientFacts attempt, never a model call and never
            // a "no challenge" (spec 185 §5).
            return baseRecord with { Status = NewsJudgmentStatus.InsufficientFacts };
        }

        var cached = await _store
            .FindCompletedAsync(cohortKey, candidate.CompanyId, bundle.FamilySetHash, ct)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            // The cache carries ONLY the verdict fields; every completeness dimension comes from BaseRecord
            // and is therefore always the CURRENT run's (the spec-182 rule: a cached verdict replayed under
            // different coverage circumstances never carries a stale derived state).
            return baseRecord with
            {
                Status = cached.Status,
                BusinessTrajectory = cached.BusinessTrajectory,
                ChallengeStrength = cached.ChallengeStrength,
                Findings = cached.Findings,
                Rationale = cached.Rationale,
                FindingsTotal = cached.FindingsTotal,
                FindingsAccepted = cached.FindingsAccepted,
                FindingsDropped = cached.FindingsDropped,
                FindingDropReasons = cached.FindingDropReasons,
                RawResponseHash = cached.RawResponseHash,
                ReusedFromJudgmentId = cached.JudgmentId,
            };
        }

        // The model request carries the company name/ticker and the canonical families ONLY (spec 185 §1):
        // no raw prose, no score, rank or label, no price, no prior judgment.
        var request = new NewsJudgmentAnalysisRequest(
            candidate.CompanyName, candidate.Ticker, bundle.Families);

        NewsJudgmentAnalysisOutcome outcome;
        try
        {
            outcome = await judge.Analyzer.AnalyzeAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Belt-and-braces: the analyzer contract types provider failures, but a throwing implementation
            // must still degrade to a recorded provider-failure attempt so other judges proceed.
            _logger.LogWarning(
                ex,
                "News-judgment reader {Judge} threw for company {Company}; recording a provider failure.",
                judge.Identity.Name,
                candidate.CompanyName);
            outcome = new NewsJudgmentAnalysisOutcome(
                NewsJudgmentAnalysisFailure.ProviderError, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }

        var record = baseRecord with { RawResponseHash = outcome.RawResponseHash };
        switch (outcome.Failure)
        {
            case NewsJudgmentAnalysisFailure.ProviderError:
                return record with
                {
                    Status = NewsJudgmentStatus.ProviderFailure,
                    FailureDetail = outcome.FailureDetail,
                };
            case NewsJudgmentAnalysisFailure.ParseError:
                return record with
                {
                    Status = NewsJudgmentStatus.ParseFailure,
                    FailureDetail = outcome.FailureDetail,
                };
            default:
            {
                var validated = NewsJudgmentValidator.Validate(outcome.Response!, bundle.Families);
                return record with
                {
                    Status = validated.Status,
                    BusinessTrajectory = validated.BusinessTrajectory,
                    ChallengeStrength = validated.ChallengeStrength,
                    Findings = validated.Findings,
                    Rationale = validated.Rationale,
                    FindingsTotal = validated.FindingsTotal,
                    FindingsAccepted = validated.FindingsAccepted,
                    FindingsDropped = validated.FindingsDropped,
                    FindingDropReasons = validated.FindingDropReasons,
                };
            }
        }
    }

    private NewsJudgmentRecord BaseRecord(
        NewsJudgmentReaderIdentity identity,
        string cohortKey,
        NewsTypingCohortRunResult cohort,
        NewsRiskCandidate candidate,
        Guid? runId,
        NewsJudgmentInputBundle bundle,
        NewsRiskCoverageEvaluation coverage,
        NewsTypingCompleteness typingCompleteness) => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: NewsJudgmentRecord.IdentityFor(
            cohortKey, candidate.CompanyId, bundle.FamilySetHash, runId),
        RunId: runId,
        CompanyId: candidate.CompanyId,
        CompanyName: candidate.CompanyName,
        Ticker: candidate.Ticker,
        JudgeName: identity.Name,
        Provider: identity.Provider,
        ModelId: identity.ModelId,
        PromptVersion: NewsJudgmentContract.PromptVersion,
        ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
        Stage1CohortKey: cohort.Reader.CohortKey,
        TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
        CohortKey: cohortKey,
        FamilySetHash: bundle.FamilySetHash,
        Families: bundle.Families
            .Select(f => new NewsJudgmentFamilyRef(
                f.FamilyId, f.RepresentativeFactId, f.MemberCount, f.DistinctPublisherCount))
            .ToList(),
        ArchiveCapture: coverage.ArchiveCapture,
        SearchEnumeration: coverage.SearchEnumeration,
        // The typing pass supplies EVERY eligible in-window observation to its extractor selection — no
        // per-company bundle cap exists on the observation supply itself (the typing-side bound is the
        // per-run call cap, which the TypingCompleteness dimension carries as Backlog).
        ObservationSupply: NewsRiskAssessmentBundle.Complete,
        TypingCompleteness: typingCompleteness,
        FamilyBundle: bundle.FamilyBundle,
        CoverageIssues: coverage.Issues,
        Status: NewsJudgmentStatus.ProviderFailure,
        BusinessTrajectory: null,
        ChallengeStrength: null,
        Findings: [],
        Rationale: null,
        FindingsTotal: 0,
        FindingsAccepted: 0,
        FindingsDropped: 0,
        FindingDropReasons: [],
        RawResponseHash: null,
        FailureDetail: null,
        Limits: _options.ToLimitsRecord(),
        ReusedFromJudgmentId: null,
        CreatedAtUtc: _timeProvider.GetUtcNow());

    /// <summary>
    /// The leaders-marker map, derived from the DESIGNATED presentation cohort only (spec 185 §4): the
    /// configured (judge, extractor) pair, declared prospectively in config and validated at startup.
    /// Returns <c>null</c> — and logs an error — when either half of the pair is absent from this run
    /// (e.g. the typing pass ran a different reader set), so the Worker keeps the honest pending markers
    /// instead of rendering from an undesignated cohort.
    /// </summary>
    private NewsJudgmentMarkerReportModel? BuildPresentationMarkers(
        IReadOnlyList<NewsJudgmentRecord> judgments,
        NewsTypingRunResult typing,
        Guid? runId,
        IReadOnlyList<NewsRiskCandidate> candidates)
    {
        var extractorCohort = typing.Cohorts.FirstOrDefault(c => string.Equals(
            c.Reader.Name, _options.PresentationExtractor, StringComparison.OrdinalIgnoreCase));
        var judge = _judges.Readers.FirstOrDefault(j => string.Equals(
            j.Identity.Name, _options.PresentationJudge, StringComparison.OrdinalIgnoreCase));
        if (extractorCohort is null || judge is null)
        {
            _logger.LogError(
                "News-judgment presentation cohort (judge '{Judge}', extractor '{Extractor}') was not "
                    + "resolvable this run; the leaders markers stay unassessed rather than rendering from "
                    + "an undesignated cohort.",
                _options.PresentationJudge,
                _options.PresentationExtractor);
            return null;
        }

        var presentationCohortKey = judge.Identity.CohortKeyFor(extractorCohort.Reader.CohortKey);
        var markers = new Dictionary<Guid, NewsJudgmentLeaderMarker>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var record = judgments.FirstOrDefault(j =>
                j.CompanyId == candidate.CompanyId
                && string.Equals(j.CohortKey, presentationCohortKey, StringComparison.Ordinal));
            markers[candidate.CompanyId] = NewsJudgmentMarkerPolicy.Derive(record, runId);
        }

        // Spec 186 §1: the store ROOT rides the model once (never per row), so the report's judgment
        // provenance appendix can name where every cited judgment id resolves.
        return new NewsJudgmentMarkerReportModel(
            JudgmentPending: false,
            Markers: markers,
            JudgmentStoreRoot: NewsJudgmentStoreLayout.RootFor(_options.OutputDirectory));
    }
}
