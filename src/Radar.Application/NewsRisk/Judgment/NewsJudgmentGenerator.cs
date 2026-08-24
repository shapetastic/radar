using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Ai;
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
    /// Runs one judgment pass over the SUPPLIED candidate plan and the typing pass's outcome. Never
    /// throws for its own failures (a judgment failure logs and returns <c>null</c>, leaving the honest
    /// <c>? unassessed</c> first render standing); caller cancellation propagates. A <c>null</c>
    /// <paramref name="typing"/> (the typing pass failed or was skipped) returns <c>null</c>: the judge
    /// structurally cannot run without stage 1.
    /// <para>
    /// Spec 187 §2: the generator no longer selects its own candidates. The Worker computes the ordered
    /// plan ONCE (<see cref="INewsJudgmentCandidatePlanner"/>) and hands the SAME immutable instance to the
    /// typing pass and to this one, so the companies typing prioritized ARE the companies judged, in the
    /// same order. A <c>null</c>/empty plan judges nothing — the pre-187 behaviour for a run with no
    /// strategy sections.
    /// </para>
    /// </summary>
    Task<NewsJudgmentRunResult?> GenerateAsync(
        Guid? runId,
        NewsJudgmentCandidatePlan? candidatePlan,
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
/// <para>
/// <b>Spec 187 §1 — the attempt bound, and its DELIBERATE asymmetry with typing.</b> The v2 validator is
/// strict, so a persistent <see cref="NewsJudgmentStatus.ValidationFailed"/> is likelier; closing typing's
/// endless retries while letting the same failed judgment call the provider every night forever would just
/// move the bill. Each (stage-2 cohort, company, family set) gets
/// <see cref="NewsJudgmentOptions.MaxJudgmentAttempts"/> CALL-PRODUCING attempts, DERIVED from the
/// insert-only store's own records (<see cref="NewsJudgmentRecord.IsCallProducingAttempt"/>) read ONCE per
/// pass — no side index, no new store. Unlike spec 187 §3's typing lane there is deliberately NO durable
/// PRE-CALL reservation ledger: this guarantee is a bound over durably RECORDED attempts plus same-run
/// idempotency, NOT crash-/disk-failure exactness across processes. A process killed between the provider
/// call and the outcome write can therefore spend one unrecorded call. That is accepted honestly because
/// judgment is ONE serial call per company per run (at most <c>MaxCompaniesPerRun</c> per cohort), while
/// typing can spend hundreds and so earns the stronger protocol.
/// </para>
/// <para>
/// <b>The family-set scope is intentional.</b> The budget is keyed on the FamilySetHash, so while the
/// typing backlog drains and a company's fact set grows, each materially changed input earns a fresh
/// budget and the bound only becomes visible once the input stabilizes. This mirrors typing's payload-hash
/// scope: a retry limit constrains repeated calls over the SAME input, never the evaluation of newly
/// available evidence.
/// </para>
/// </summary>
public sealed class NewsJudgmentGenerator : INewsJudgmentGenerator
{
    /// <summary>
    /// Spec 187 §7: how many ATTEMPTED provider calls one (judge × stage-1 cohort) pass makes between
    /// bounded progress lines. 5 — a fifth of typing's 25 — because a judgment pass is an order of
    /// magnitude smaller (18 calls on the first live run against 200 typings), so a 25-call boundary would
    /// have emitted nothing at all before the stage ended. The final partial batch is always emitted.
    /// </summary>
    private const int JudgmentProgressBatchSize = 5;

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
        NewsJudgmentCandidatePlan? candidatePlan,
        NewsTypingRunResult? typing,
        CancellationToken ct)
    {
        try
        {
            return await GenerateCoreAsync(runId, candidatePlan, typing, ct).ConfigureAwait(false);
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
        NewsJudgmentCandidatePlan? candidatePlan,
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

        // Spec 187 §2: the plan is CONSUMED, never recomputed. Selection policy lives in exactly one place
        // (NewsRiskCandidateSelector, invoked once per run by INewsJudgmentCandidatePlanner), so the typing
        // pass's candidate lane and this loop cannot disagree about who the leaders are.
        var candidates = (candidatePlan ?? NewsJudgmentCandidatePlan.Empty).Candidates;

        var batch = typing.NewsObservationBatchId is { } batchId
            ? await _batchReader.GetBatchAsync(batchId, ct).ConfigureAwait(false)
            : null;

        // Spec 187 §7: the MONOTONIC stage anchor for every "elapsed" number the progress lines report.
        var stageStartTimestamp = _timeProvider.GetTimestamp();

        // Spec 187 §1: the PRE-PASS store snapshot is read ONCE and is the sole authority for how many
        // hosted calls each (cohort, company, family set) has already spent — deterministic and clock-free
        // (AD-3), so the standalone attempt ordinal below cannot drift within a pass.
        var history = JudgmentAttemptHistory.FromStore(
            await _store.GetAllAsync(ct).ConfigureAwait(false), runId);

        var judgments = new List<NewsJudgmentRecord>();
        var unpersisted = new HashSet<(Guid CompanyId, string CohortKey)>();
        var exhaustedByCohort = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cohort in typing.Cohorts)
        {
            foreach (var judge in _judges.Readers)
            {
                // Spec 187 §7: latency is accumulated per (judge × stage-1 cohort) — the granularity the
                // spec asks for and the only one that means anything, since a judge's throughput against
                // one extractor's families says nothing about another's. Purely observational: no branch
                // below reads these, nothing here is persisted into an id or a fingerprint, and selection
                // was fixed before this loop began (AD-3).
                var judgeCohortKey = judge.Identity.CohortKeyFor(cohort.Reader.CohortKey);
                var timings = new ProviderCallTimings();
                var attemptedCalls = 0;
                var persistedJudged = 0;
                var providerFailures = 0;
                var parseFailures = 0;
                var validationFailures = 0;

                void LogJudgmentProgress() => _logger.LogInformation(
                    "News-judgment judge {Judge} ({Cohort}) progress: {Attempted}/{Candidates} call(s) "
                        + "attempted, {Persisted} persisted judged verdict(s), failures "
                        + "{ProviderFailures} provider / {ParseFailures} parse / {ValidationFailures} "
                        + "validation; stage elapsed {ElapsedMs} ms, mean call {MeanMs} ms, max call "
                        + "{MaxMs} ms.",
                    judge.Identity.Name,
                    judgeCohortKey,
                    attemptedCalls,
                    candidates.Count,
                    persistedJudged,
                    providerFailures,
                    parseFailures,
                    validationFailures,
                    _timeProvider.GetElapsedTime(stageStartTimestamp).TotalMilliseconds.ToString(
                        "F0", CultureInfo.InvariantCulture),
                    timings.MeanMs.ToString("F1", CultureInfo.InvariantCulture),
                    timings.Max.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));

                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var record = await JudgeOneAsync(judge, cohort, candidate, runId, batch, history, ct)
                        .ConfigureAwait(false);
                    if (record.Status == NewsJudgmentStatus.AttemptsExhausted)
                    {
                        exhaustedByCohort[record.CohortKey] =
                            exhaustedByCohort.GetValueOrDefault(record.CohortKey) + 1;
                    }

                    // A non-null duration IS the record of "a hosted call was made" — the same fact the
                    // persisted field carries — so the counters and the store cannot disagree about how
                    // many calls this pass spent (spec 187 §7).
                    if (record.ProviderDurationMs is { } durationMs)
                    {
                        timings.Record(TimeSpan.FromMilliseconds(durationMs));
                        attemptedCalls++;
                    }

                    switch (record.Status)
                    {
                        case NewsJudgmentStatus.ProviderFailure:
                            providerFailures++;
                            break;
                        case NewsJudgmentStatus.ParseFailure:
                            parseFailures++;
                            break;
                        case NewsJudgmentStatus.ValidationFailed:
                            validationFailures++;
                            break;
                        default:
                            break;
                    }

                    // Spec 187 §1: the durable write's OUTCOME is checked. An unpersisted result is not a
                    // durable judgment: it never joins the run result and never reaches a leaders row as a
                    // judged/challenged state — the row says `not-persisted` instead.
                    if (!await _store.WriteAsync(record, ct).ConfigureAwait(false))
                    {
                        _logger.LogWarning(
                            "News-judgment record {JudgmentId} for company {Company} (cohort {Cohort}) "
                                + "could not be persisted; it is NOT presented as a durable judgment and "
                                + "the row renders unassessed.",
                            record.JudgmentId,
                            candidate.CompanyName,
                            record.CohortKey);
                        unpersisted.Add((candidate.CompanyId, record.CohortKey));
                    }
                    else
                    {
                        judgments.Add(record);
                        if (record.Status == NewsJudgmentStatus.Judged)
                        {
                            persistedJudged++;
                        }
                    }

                    if (attemptedCalls > 0 && attemptedCalls % JudgmentProgressBatchSize == 0)
                    {
                        LogJudgmentProgress();
                    }
                }

                // The FINAL PARTIAL BATCH. A pass that made no call emits no progress line at all — the
                // summary below says "0 provider call(s)" rather than a progress line implying work.
                if (attemptedCalls > 0 && attemptedCalls % JudgmentProgressBatchSize != 0)
                {
                    LogJudgmentProgress();
                }

                // The judge's FINAL provider-latency summary: deterministic nearest-rank percentiles over
                // THIS pass's in-memory durations only. No model text, no secret — identity and numbers.
                _logger.LogInformation(
                    "News-judgment judge {Judge} ({Cohort}) provider timing: {Timing}.",
                    judge.Identity.Name,
                    judgeCohortKey,
                    timings.Summarize().Describe());
            }
        }

        // One aggregated Warning per cohort (the spec-145 precedent): the bound must be VISIBLE, but a
        // per-company line would drown the log once a cohort's inputs stabilize.
        foreach (var (cohortKey, exhausted) in exhaustedByCohort.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            _logger.LogWarning(
                "News-judgment cohort {Cohort}: {Exhausted} company/company-family-set(s) have spent all "
                    + "{MaxAttempts} judgment attempt(s); NO model call was made for them this run and "
                    + "their rows render '? unassessed (retries-exhausted)'. A materially changed fact "
                    + "family set, or a prompt/schema/stage-1 cohort change, earns a fresh budget.",
                cohortKey,
                exhausted,
                _options.MaxJudgmentAttempts);
        }

        var markers = BuildPresentationMarkers(judgments, typing, runId, candidates, unpersisted);

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
        JudgmentAttemptHistory history,
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

        var attemptKey = (cohortKey, candidate.CompanyId, bundle.FamilySetHash);
        var priorAttempts = history.CallProducingAttempts(attemptKey);
        var baseRecord = BaseRecord(
            judge.Identity,
            cohortKey,
            cohort,
            candidate,
            runId,
            bundle,
            coverage,
            typingCompleteness,
            attemptNumber: priorAttempts + 1);

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
                TrajectoryFactIds = cached.TrajectoryFactIds,
                ReusedFromJudgmentId = cached.JudgmentId,
            };
        }

        // Spec 187 §1 — same-run idempotency: this run already spent a call on this exact input, so it is
        // REUSED for presentation and no second call is made. (The null-run path has no run to be "the
        // same" as: it mints a fresh standalone#N attempt identity instead, the spec-186 §2 precedent.)
        if (runId is not null && history.SameRunAttempt(attemptKey) is { } sameRun)
        {
            _logger.LogDebug(
                "News-judgment for company {Company} (cohort {Cohort}) already has a persisted attempt for "
                    + "run {RunId}; reusing it for presentation without a second model call.",
                candidate.CompanyName,
                cohortKey,
                runId);
            return sameRun;
        }

        if (priorAttempts >= _options.MaxJudgmentAttempts)
        {
            // The bound: NO call, and a same-run record that SAYS so. It is not a completed judgment, it
            // does not itself count as an attempt, and it carries no model result.
            return baseRecord with
            {
                JudgmentId = NewsJudgmentRecord.ExhaustionIdentityFor(
                    cohortKey, candidate.CompanyId, bundle.FamilySetHash, runId),
                Status = NewsJudgmentStatus.AttemptsExhausted,
                FailureDetail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"attempts-exhausted: {priorAttempts} call-producing judgment attempt(s) have already "
                        + $"been recorded for this cohort/company/family set, reaching the "
                        + $"{_options.MaxJudgmentAttempts}-attempt bound; no model call was made."),
            };
        }

        // The model request carries the company name/ticker and the canonical families ONLY (spec 185 §1):
        // no raw prose, no score, rank or label, no price, no prior judgment.
        var request = new NewsJudgmentAnalysisRequest(
            candidate.CompanyName, candidate.Ticker, bundle.Families);

        // Spec 187 §7: the provider call is bracketed by the injected TimeProvider's MONOTONIC timestamp
        // APIs. The measurement covers the throwing path too (the elapsed read sits AFTER the catch), so a
        // slow failure — the case most worth seeing — records its duration rather than losing it.
        var callStartTimestamp = _timeProvider.GetTimestamp();
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

        var record = baseRecord with
        {
            RawResponseHash = outcome.RawResponseHash,
            ProviderDurationMs = _timeProvider.GetElapsedTime(callStartTimestamp).TotalMilliseconds,
        };
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
                    // Spec 187 §1: the supplied facts the judge said establish the trajectory. A v2
                    // Judged record always carries a non-null list (empty iff Unknown); a failed
                    // validation carries the empty set, which is honest rather than "not recorded".
                    TrajectoryFactIds = validated.TrajectoryFactIds,
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
        NewsTypingCompleteness typingCompleteness,
        int attemptNumber) => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: NewsJudgmentRecord.IdentityFor(
            cohortKey, candidate.CompanyId, bundle.FamilySetHash, runId, attemptNumber),
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
        IReadOnlyList<NewsRiskCandidate> candidates,
        IReadOnlySet<(Guid CompanyId, string CohortKey)> unpersisted)
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
            if (record is null && unpersisted.Contains((candidate.CompanyId, presentationCohortKey)))
            {
                // Spec 187 §1: the judgment existed but its durable write failed. Saying "not a candidate"
                // would be a false claim about selection, and presenting the result would claim a durability
                // Radar does not have — so the row names the actual condition.
                markers[candidate.CompanyId] = new NewsJudgmentLeaderMarker(
                    NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.NotPersisted);
                continue;
            }

            markers[candidate.CompanyId] = NewsJudgmentMarkerPolicy.Derive(record, runId);
        }

        // Spec 186 §1: the store ROOT rides the model once (never per row), so the report's judgment
        // provenance appendix can name where every cited judgment id resolves.
        return new NewsJudgmentMarkerReportModel(
            JudgmentPending: false,
            Markers: markers,
            JudgmentStoreRoot: NewsJudgmentStoreLayout.RootFor(_options.OutputDirectory));
    }

    /// <summary>
    /// Spec 187 §1's DERIVED attempt accounting: the pre-pass snapshot of the insert-only judgment store,
    /// indexed by (stage-2 cohort key, company, family-set hash). Two facts per key, and only two:
    /// <list type="bullet">
    /// <item><b>how many HOSTED CALLS have been spent</b> — <c>Judged</c>, <c>ValidationFailed</c>,
    /// <c>ProviderFailure</c> and <c>ParseFailure</c> each cost one; <c>InsufficientFacts</c>, a cache
    /// reuse and an <c>AttemptsExhausted</c> marker cost none (see
    /// <see cref="NewsJudgmentRecord.IsCallProducingAttempt"/>); and</item>
    /// <item><b>whether THIS run already spent one</b> — same-run idempotency, so re-entering a run reuses
    /// the persisted attempt for presentation instead of calling the provider a second time.</item>
    /// </list>
    /// Derived, never stored: no side index and no second source of truth to keep in sync with the records
    /// the store already holds. Keys compare ordinally (a cohort key and a hash are exact tokens).
    /// </summary>
    private sealed class JudgmentAttemptHistory
    {
        private readonly Dictionary<(string CohortKey, Guid CompanyId, string FamilySetHash), Entry> _byKey;

        private JudgmentAttemptHistory(
            Dictionary<(string CohortKey, Guid CompanyId, string FamilySetHash), Entry> byKey) =>
            _byKey = byKey;

        public static JudgmentAttemptHistory FromStore(
            IReadOnlyList<NewsJudgmentRecord> records, Guid? runId)
        {
            var byKey = new Dictionary<(string, Guid, string), Entry>();
            foreach (var record in records)
            {
                if (!record.IsCallProducingAttempt)
                {
                    continue;
                }

                var key = (record.CohortKey, record.CompanyId, record.FamilySetHash);
                var current = byKey.GetValueOrDefault(key, Entry.None);
                byKey[key] = new Entry(
                    CallProducingAttempts: current.CallProducingAttempts + 1,
                    // The store enumerates deterministically (CreatedAtUtc, JudgmentId), so "the last
                    // same-run attempt" is stable across processes (AD-3).
                    SameRunAttempt: runId is { } id && record.RunId == id
                        ? record
                        : current.SameRunAttempt);
            }

            return new JudgmentAttemptHistory(byKey);
        }

        public int CallProducingAttempts(
            (string CohortKey, Guid CompanyId, string FamilySetHash) key) =>
            _byKey.GetValueOrDefault(key, Entry.None).CallProducingAttempts;

        public NewsJudgmentRecord? SameRunAttempt(
            (string CohortKey, Guid CompanyId, string FamilySetHash) key) =>
            _byKey.GetValueOrDefault(key, Entry.None).SameRunAttempt;

        private readonly record struct Entry(
            int CallProducingAttempts, NewsJudgmentRecord? SameRunAttempt)
        {
            public static readonly Entry None = new(0, null);
        }
    }
}
