using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Ai;
using Radar.Application.Filings;
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
    IReadOnlyDictionary<string, int> Stage1FactsDroppedByCohort,
    // Spec 194 §1.2: what the judgment-signal materializer did with THIS pass's judgments. TRAILING and
    // NULLABLE — `null` means the step was NOT ATTEMPTED (a pre-194 composition, or a graph that registers
    // no INewsJudgmentSignalMaterializer), never "attempted and produced nothing", which is an all-zero
    // summary. The generator itself never sets it: materialization happens AFTER the judgment pass, so the
    // Worker attaches the summary to the returned result before the live artifact is built.
    NewsJudgmentSignalMaterializationSummary? SignalMaterialization = null);

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
/// <para>
/// <b>Spec 188 §1 — durable call PROVENANCE is not current-pass ACTIVITY.</b> The spec-187 §7 counters
/// (attempted calls, latency samples, provider/parse/validation failures, persisted judged successes and
/// the every-fifth-call progress boundary) are driven ONLY by <see cref="JudgmentPassOutcome"/>, the
/// transient per-invocation record of whether THIS pass invoked the analyzer. They are never inferred from
/// <see cref="NewsJudgmentRecord.ProviderDurationMs"/>: a same-run reused attempt legitimately carries the
/// duration AND the failure status of the original call, so reading that field as "a call happened" replayed
/// old latency and old failures as this pass's — false on exactly the rerun path the telemetry exists to
/// explain. An all-reuse pass therefore makes no call, emits no progress line, and reports an explicit
/// zero-call summary, while every reused record keeps its original duration for audit.
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

    // OPT-IN reported-metrics ledger (spec 215 §2). Null when no ledger is registered: every judgment is
    // then assembled with zero references and every family-set hash, prompt and record is byte-identical
    // to a reference-free judgment. MS DI supplies the null default when the seam is not registered.
    private readonly IReportedMetricStore? _reportedMetrics;

    public NewsJudgmentGenerator(
        INewsObservationBatchReader batchReader,
        NewsJudgmentReaderSet judges,
        INewsJudgmentStore store,
        NewsJudgmentOptions options,
        TimeProvider timeProvider,
        ILogger<NewsJudgmentGenerator> logger,
        IReportedMetricStore? reportedMetrics = null)
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
        _reportedMetrics = reportedMetrics;
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
        // (NewsRiskCandidateSelector for the spec-179 §3 depth cohort, NewsJudgmentCoveragePolicy for the
        // spec-219 §1 breadth cohort, both invoked once per run by INewsJudgmentCandidatePlanner), so the
        // typing pass's candidate lane and this loop cannot disagree about who the leaders are.
        var plan = candidatePlan ?? NewsJudgmentCandidatePlan.Empty;
        var candidates = plan.PlannedCandidates;

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
        // Spec 219 §1: the breadth candidates this pass SKIPPED for want of typed facts. They persist no
        // record, so without this set the marker policy would see "no record" and render
        // `not-a-candidate` — a FALSE claim about selection (the company WAS planned), and exactly the
        // failure mode spec 187 §1 introduced `not-persisted` to avoid. The row names the real condition.
        var skippedNoTypedFacts = new HashSet<(Guid CompanyId, string CohortKey)>();
        var exhaustedByCohort = new Dictionary<string, int>(StringComparer.Ordinal);
        var overSoftLimitRationalesByCohort = new Dictionary<string, int>(StringComparer.Ordinal);
        var prefixExpansionsByCohort = new Dictionary<string, (int Expansions, int Judgments)>(
            StringComparer.Ordinal);
        var levelOnlyBasisByCohort = new Dictionary<string, (int LevelOnly, int ReferenceSupported, int Directional)>(
            StringComparer.Ordinal);
        var referencesSuppliedByCohort = new Dictionary<string, (int WithReferences, int Judgments)>(
            StringComparer.Ordinal);
        // Spec 223 §3: per cohort, the called judgments handed ZERO references, split by WHY on separate
        // axes. "Ledger empty" (the ledger holds nothing at all) and "no matching reference" (the ledger
        // holds values but none match this company's facts) must never share a counter.
        var noReferencesByCohort = new Dictionary<string, ReferenceAbsenceCounters>(StringComparer.Ordinal);

        // Spec 215 §2: each candidate's ledger is read ONCE per pass (not once per judge × cohort) and a
        // read failure degrades to "no references" — counted, and reported once per company, never a
        // silent empty. Without a registered ledger nothing is read and nothing is counted.
        // Spec 223 §3: the companies whose ledger could NOT be read are kept so their zero-reference
        // judgments classify as NotRecorded rather than as a measured "ledger empty".
        var unreadableLedgers = new HashSet<Guid>();
        var ledgerByCompany = await LoadLedgersAsync(
            [.. candidates.Select(c => c.Candidate)], unreadableLedgers, ct).ConfigureAwait(false);

        // Spec 223 §3: the GLOBAL ledger inventory, read ONCE per pass, is what distinguishes "the ledger
        // holds nothing at all" from "nothing matches this company". Not registered, or a failed read, is
        // null — and null classifies every zero-reference judgment as NotRecorded, never as empty.
        var ledgerInventory = await InventoryLedgerAsync(ct).ConfigureAwait(false);

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
                // Spec 219 §4: the coverage ledger for THIS (judge × stage-1 cohort) pass. ONE aggregated
                // line per pass, never one per company (the spec-145 precedent).
                var coverage = new JudgmentCoverageCounters();
                // Spec 220 §3: the comparison-basis ledger for the same pass — one more aggregated line.
                var basisCounts = new JudgmentBasisCounters();
                // Spec 221 §3: the business-signal ledger for the same pass — two more aggregated lines.
                var businessSignal = new JudgmentBusinessSignalCounters();
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

                foreach (var planned in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var candidate = planned.Candidate;
                    var judged = await JudgeOneAsync(
                        judge, cohort, planned, runId, batch, history,
                        ledgerByCompany.GetValueOrDefault(candidate.CompanyId, []), ct).ConfigureAwait(false);
                    if (judged is not { } outcome)
                    {
                        // Spec 219 §1/§4: a BREADTH candidate this stage-1 cohort holds NO families for. It
                        // is a company with nothing to read — not an error and not a failure — so it is
                        // SKIPPED AND COUNTED, and NO record is persisted. Writing ~80 InsufficientFacts
                        // non-attempts per run per cohort would bury the store and the artifact in
                        // non-events while saying nothing the counter does not say. The DEPTH path still
                        // persists InsufficientFacts exactly as before, because a leader row that is about
                        // to be shown to a human must be able to name why it was not assessed.
                        coverage.SkippedNoTypedFacts++;
                        skippedNoTypedFacts.Add((candidate.CompanyId, judgeCohortKey));
                        continue;
                    }

                    var record = outcome.Record;
                    coverage.Observe(planned.Depth, record, outcome);
                    basisCounts.Observe(planned.Depth, record, outcome);
                    businessSignal.Observe(planned.Depth, record);
                    if (record.Status == NewsJudgmentStatus.AttemptsExhausted)
                    {
                        exhaustedByCohort[record.CohortKey] =
                            exhaustedByCohort.GetValueOrDefault(record.CohortKey) + 1;
                    }

                    // Spec 188 §1: CURRENT-PASS activity comes from the pass-local outcome, never from the
                    // record's persisted duration. A same-run reused attempt legitimately carries the
                    // non-null duration and the failure status of the ORIGINAL call, so inferring "a call
                    // happened" from that field replayed old latency and old failures as current ones on
                    // exactly the rerun path this telemetry exists to explain.
                    var callDuration = outcome.ProviderCallDurationThisPass;
                    var madeProviderCall = callDuration is not null;
                    if (callDuration is { } measured)
                    {
                        timings.Record(measured);
                        attemptedCalls++;

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
                    }

                    // Spec 192 §2: the soft rationale bound no longer discards findings, so it becomes a
                    // MEASURED prompt-quality signal instead — aggregated per cohort (the spec-145
                    // precedent), never one line per judgment. Counted ONLY for a judgment this pass
                    // actually called the provider for (spec 188 §1): a reused verdict legitimately carries
                    // the ORIGINAL call's rationale length, and replaying it here would report old prose as
                    // current activity on exactly the re-run path this telemetry exists to explain.
                    if (madeProviderCall && record.RationaleOverSoftLimit == true)
                    {
                        overSoftLimitRationalesByCohort[record.CohortKey] =
                            overSoftLimitRationalesByCohort.GetValueOrDefault(record.CohortKey) + 1;
                    }

                    // Spec 197 §2.2: the citation-recovery pressure, aggregated ONCE per cohort (the
                    // spec-145 precedent). Counted ONLY for a judgment this pass actually called the
                    // provider for (spec 188 §1): a reused or cached verdict legitimately carries the
                    // ORIGINAL call's expansion count, and replaying it here would report an old provider's
                    // shorthand as current behaviour on exactly the re-run path this telemetry explains.
                    if (madeProviderCall && record.FactIdPrefixExpansionCount is { } expansions
                        && expansions > 0)
                    {
                        var running = prefixExpansionsByCohort.GetValueOrDefault(record.CohortKey);
                        prefixExpansionsByCohort[record.CohortKey] =
                            (running.Expansions + expansions, running.Judgments + 1);
                    }

                    // Spec 214 §2: the level-read count, aggregated ONCE per cohort (the spec-145 precedent,
                    // never a line per judgment), over the directional judgments this pass actually CALLED
                    // the provider for (spec 188 §1 — a reused verdict carries its ORIGINAL basis and would
                    // report old prose as current behaviour). The denominator is every directional Judged
                    // call, so the share is legible beside the count.
                    if (madeProviderCall && record.Status == NewsJudgmentStatus.Judged
                        && record.TrajectoryBasis is { } basis)
                    {
                        var running = levelOnlyBasisByCohort.GetValueOrDefault(record.CohortKey);
                        levelOnlyBasisByCohort[record.CohortKey] = (
                            running.LevelOnly + (basis == NewsTrajectoryBasis.LevelOnly ? 1 : 0),
                            running.ReferenceSupported + (basis == NewsTrajectoryBasis.ReferenceSupported ? 1 : 0),
                            running.Directional + 1);
                    }

                    // Spec 215 §2: how many judgments this pass actually CALLED for were handed at least one
                    // company-reported reference value — the projection's live hit rate, aggregated once
                    // per cohort (spec 188 §1: a reused verdict carries its ORIGINAL reference set and is
                    // not current activity).
                    if (madeProviderCall && record.ReferenceIds is { } projected)
                    {
                        var running = referencesSuppliedByCohort.GetValueOrDefault(record.CohortKey);
                        referencesSuppliedByCohort[record.CohortKey] = (
                            running.WithReferences + (projected.Count > 0 ? 1 : 0),
                            running.Judgments + 1);

                        // Spec 223 §3: a called judgment handed ZERO references is classified by WHY, on
                        // separate axes, from the projection's own reason (threaded in-process from the
                        // bundle — never re-derived from a ledger that may since have grown) and the
                        // pass-level inventory.
                        if (projected.Count == 0)
                        {
                            if (!noReferencesByCohort.TryGetValue(record.CohortKey, out var absence))
                            {
                                absence = new ReferenceAbsenceCounters();
                                noReferencesByCohort[record.CohortKey] = absence;
                            }

                            absence.Observe(ClassifyReferenceAbsence(
                                outcome.ReferenceAbsenceReason,
                                ledgerInventory,
                                ledgerRegistered: _reportedMetrics is not null,
                                companyLedgerUnreadable: unreadableLedgers.Contains(candidate.CompanyId)));
                        }
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
                        coverage.Persisted(planned.Depth, record);
                        businessSignal.Persisted(planned.Depth, record);

                        // A REUSED judged verdict still reaches the run result and presentation, but it is
                        // not a judged verdict this pass newly produced (spec 188 §1).
                        if (madeProviderCall && record.Status == NewsJudgmentStatus.Judged)
                        {
                            persistedJudged++;
                        }
                    }

                    // The boundary is evaluated ONLY immediately after a current provider call: otherwise
                    // any later no-call candidate (same-run reuse, cache reuse, InsufficientFacts,
                    // AttemptsExhausted) re-emitted the same `5/…` line while nothing had happened.
                    if (madeProviderCall && attemptedCalls % JudgmentProgressBatchSize == 0)
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

                // SPEC 219 §4 — the PER-COHORT coverage line: ONE aggregated line per (judge × stage-1
                // cohort), never one per company. It carries ONLY facts that are per-cohort. The RUN-LEVEL
                // facts (universe size, the capacity valve and what it dropped) are emitted ONCE per run,
                // below the cohort loops — repeating them here made a two-cohort run state them twice and
                // invited a reader to double-count. Every number is measured over THIS pass, and a zero is
                // reported AS a zero rather than omitted.
                _logger.LogInformation(
                    "News-judgment judge {Judge} ({Cohort}) coverage ({CoveragePolicy}): {Accounted} of "
                        + "{Planned} planned candidate(s) accounted for — {WithFacts} with typed facts in "
                        + "window, {NoFactsRecorded} recorded with none, {SkippedNoFacts} skipped with "
                        + "none, {FamiliesNotRecorded} whose family accounting was not recorded; judged "
                        + "{JudgedBreadth} breadth + {JudgedFull} full = {JudgedTotal}; families "
                        + "{FamiliesSupplied} supplied of {FamiliesAvailable} available, "
                        + "{FamiliesWithheld} withheld by budget (breadth cap {BreadthCap}, full cap "
                        + "{FullCap}); reused {ReusedBreadth} breadth / {ReusedFull} full; failed "
                        + "{FailedBreadth} breadth / {FailedFull} full, of which validation-failed "
                        + "{ValidationBreadth} breadth / {ValidationFull} full.",
                    judge.Identity.Name,
                    judgeCohortKey,
                    NewsJudgmentCoveragePolicy.Version,
                    coverage.Accounted,
                    candidates.Count,
                    coverage.WithTypedFacts,
                    coverage.RecordedWithNoTypedFacts,
                    coverage.SkippedNoTypedFacts,
                    coverage.FamiliesNotRecorded,
                    coverage.JudgedBreadth,
                    coverage.JudgedFull,
                    coverage.JudgedBreadth + coverage.JudgedFull,
                    coverage.FamiliesSupplied,
                    coverage.FamiliesAvailable,
                    coverage.FamiliesWithheldByBudget,
                    _options.MaxFamiliesPerBreadthJudgment,
                    _options.MaxFamiliesPerJudgment,
                    coverage.ReusedBreadth,
                    coverage.ReusedFull,
                    coverage.FailedBreadth,
                    coverage.FailedFull,
                    coverage.ValidationFailedBreadth,
                    coverage.ValidationFailedFull);

                // SPEC 220 §3 — the PER-COHORT comparison-basis line, emitted BESIDE the unchanged spec-219
                // coverage line (never replacing it), ONE per (judge × stage-1 cohort), never one per
                // company. It is the number that says whether the basis-first ordering changed what the
                // judge saw: withholding NotQuantified boilerplate is a different fact from withholding
                // stated comparisons. Every class is named and a zero is reported AS a zero; a record that
                // did not record its basis accounting is counted on its own axis, never folded in as 0.
                _logger.LogInformation(
                    "News-judgment judge {Judge} ({Cohort}) families by comparison basis ({FamilyOrdering}, "
                        + "{ClassifierVersion}): FamiliesByBasisAvailable breadth [{AvailableBreadth}] full "
                        + "[{AvailableFull}]; FamiliesByBasisSupplied breadth [{SuppliedBreadth}] full "
                        + "[{SuppliedFull}]; FamiliesWithheldByBudget breadth [{WithheldBreadth}] full "
                        + "[{WithheldFull}]; {BasisNotRecorded} assembled input(s) whose basis accounting was "
                        + "not recorded; ChallengeStrengthNotStated {NotStatedBreadth} breadth / "
                        + "{NotStatedFull} full judgment(s) called this pass accepted with surviving findings "
                        + "and no stated strength (persisted as not recorded, never 0).",
                    judge.Identity.Name,
                    judgeCohortKey,
                    NewsJudgmentFamilyOrdering.Version,
                    StatementComparisonClassifier.Version,
                    basisCounts.AvailableBreadth.Describe(),
                    basisCounts.AvailableFull.Describe(),
                    basisCounts.SuppliedBreadth.Describe(),
                    basisCounts.SuppliedFull.Describe(),
                    basisCounts.WithheldBreadth.Describe(),
                    basisCounts.WithheldFull.Describe(),
                    basisCounts.NotRecorded,
                    basisCounts.ChallengeStrengthNotStatedBreadth,
                    basisCounts.ChallengeStrengthNotStatedFull);

                // SPEC 221 §3 — the PER-COHORT business-signal line, BESIDE (never replacing) the unchanged
                // spec-219 coverage and spec-220 basis lines, ONE per (judge × stage-1 cohort). Two populations,
                // each named in the text: the FAMILY counts cover every assembled input (reuse included — the
                // families describe THIS run's assembly, the spec-220 rule); the VERDICT counts cover exactly the
                // judged verdicts the coverage line counts (durably persisted, called or reused), so the two lines
                // reconcile. A record that did not record its accounting lands on its own axis, never a zero.
                _logger.LogInformation(
                    "News-judgment judge {Judge} ({Cohort}) business signal ({FamilyOrdering}): over every "
                        + "assembled input — FamiliesNonBusinessAvailable {NonBusinessAvailableBreadth} breadth / "
                        + "{NonBusinessAvailableFull} full; FamiliesNonBusinessSupplied {NonBusinessSuppliedBreadth} "
                        + "breadth / {NonBusinessSuppliedFull} full; FamiliesNonBusinessDemotedBySelection "
                        + "{DemotedBreadth} breadth / {DemotedFull} full; FamiliesWithNoEventTypes {NoTypesBreadth} "
                        + "breadth / {NoTypesFull} full available (not demoted — counted as business); "
                        + "{FamilyAccountingNotRecorded} assembled input(s) whose business-signal accounting was not "
                        + "recorded. Over the {VerdictsBreadth} breadth / {VerdictsFull} full judged verdict(s) on "
                        + "the coverage line (persisted, called or reused) — JudgmentsWithNoDirectionalBasisSupplied "
                        + "{NoBasisBreadth} breadth / {NoBasisFull} full; JudgmentsNoBusinessSignal "
                        + "{NoBusinessSignalBreadth} breadth / {NoBusinessSignalFull} full; "
                        + "ClassifierSaidDirectionalJudgeSaidNoBusinessSignal {DisagreeBreadth} breadth / "
                        + "{DisagreeFull} full; Deteriorating {DeterioratingBreadth} breadth / {DeterioratingFull} "
                        + "full; Unknown {UnknownBreadth} breadth / {UnknownFull} full; {ProfileNotRecorded} judged "
                        + "verdict(s) whose supplied-basis profile was not recorded (excluded from the two "
                        + "profile-derived counts, never counted as 0).",
                    judge.Identity.Name,
                    judgeCohortKey,
                    NewsJudgmentFamilyOrdering.Version,
                    businessSignal.NonBusinessAvailableBreadth,
                    businessSignal.NonBusinessAvailableFull,
                    businessSignal.NonBusinessSuppliedBreadth,
                    businessSignal.NonBusinessSuppliedFull,
                    businessSignal.DemotedBreadth,
                    businessSignal.DemotedFull,
                    businessSignal.NoEventTypesBreadth,
                    businessSignal.NoEventTypesFull,
                    businessSignal.FamilyAccountingNotRecorded,
                    businessSignal.VerdictsBreadth,
                    businessSignal.VerdictsFull,
                    businessSignal.NoDirectionalBasisBreadth,
                    businessSignal.NoDirectionalBasisFull,
                    businessSignal.NoBusinessSignalBreadth,
                    businessSignal.NoBusinessSignalFull,
                    businessSignal.DisagreementBreadth,
                    businessSignal.DisagreementFull,
                    businessSignal.DeterioratingBreadth,
                    businessSignal.DeterioratingFull,
                    businessSignal.UnknownBreadth,
                    businessSignal.UnknownFull,
                    businessSignal.ProfileNotRecorded);

                // SPEC 221 §3 — the residual Unknown WORKLIST, NAMED: every judged Unknown verdict this pass
                // (persisted, called or reused) as `TICKER (judgmentId)`, ordinal-sorted, split breadth / full.
                // A count alone reproduces the aggregate spec 221 exists to break; "none" is stated, never omitted.
                _logger.LogInformation(
                    "News-judgment judge {Judge} ({Cohort}) residual Unknown worklist: {UnknownBreadth} breadth / "
                        + "{UnknownFull} full judged Unknown verdict(s) — a supplied business fact bore on a "
                        + "direction the judge could not resolve. Breadth: {WorklistBreadth}. Full: {WorklistFull}. "
                        + "`scripts/audit-miss-diagnosis.ps1 -Ticker <T> -ScoreDate <D>` reconstructs what was read.",
                    judge.Identity.Name,
                    judgeCohortKey,
                    businessSignal.UnknownBreadth,
                    businessSignal.UnknownFull,
                    JudgmentBusinessSignalCounters.Describe(businessSignal.UnknownWorklistBreadth),
                    JudgmentBusinessSignalCounters.Describe(businessSignal.UnknownWorklistFull));
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

        // Spec 192 §2: one Information line per cohort that produced one — Information, not Warning,
        // because a long rationale is a prompt-tuning fact, not a fault: the findings were validated on
        // their own merits and the full text is on the record. Cohorts with none say nothing.
        foreach (var (cohortKey, overLong) in overSoftLimitRationalesByCohort.OrderBy(
            e => e.Key, StringComparer.Ordinal))
        {
            _logger.LogInformation(
                "News-judgment cohort {Cohort}: {OverLong} judgment(s) called this pass carried a rationale "
                    + "longer than the {SoftLimit}-character soft bound. The full rationale is PERSISTED, "
                    + "findings were validated on their own merits, and nothing was truncated — this is a "
                    + "prompt-tuning signal, not a failure.",
                cohortKey,
                overLong,
                NewsJudgmentValidator.MaxRationaleLength);
        }

        // Spec 197 §2.2: one Information line per cohort that expanded at least one citation — Information,
        // not Warning, because a recovered citation is a measured PROMPT-CONTRACT signal, not a fault: the
        // referent was deterministic in the supplied set or the citation would have failed. A cohort whose
        // judge quoted every id in full says nothing, so silence here means "measured zero", and the
        // per-record count says so durably.
        foreach (var (cohortKey, measured) in prefixExpansionsByCohort.OrderBy(
            e => e.Key, StringComparer.Ordinal))
        {
            _logger.LogInformation(
                "News-judgment cohort {Cohort}: {Expansions} FactId citation(s) across {Judgments} "
                    + "judgment(s) called this pass were shortened by the judge and deterministically "
                    + "expanded to the single supplied fact each prefixed (minimum "
                    + "{MinimumPrefixLength} hexadecimal characters, one referent or the citation fails). "
                    + "Prompt {PromptVersion} asks for complete 36-character FactIds; a persistent count "
                    + "here is prompt-tuning evidence, not a silent repair.",
                cohortKey,
                measured.Expansions,
                measured.Judgments,
                NewsJudgmentCitationResolver.MinimumPrefixLength,
                NewsJudgmentContract.PromptVersion);
        }

        // Spec 214 §2: one Information line per cohort that produced at least one directional call — the
        // LevelOnly count is the measured rate at which the judge still reads a level as a trend under
        // prompt rule 11, and every such judgment is persisted verbatim, marked, and minted into NO signal
        // by the materializer's allowlist. A cohort with zero directional calls says nothing; a cohort
        // with directional calls and zero LevelOnly says "0", which is a measured zero.
        foreach (var (cohortKey, measured) in levelOnlyBasisByCohort.OrderBy(
            e => e.Key, StringComparer.Ordinal))
        {
            _logger.LogInformation(
                "News-judgment cohort {Cohort}: {LevelOnly} of {Directional} directional judgment(s) called "
                    + "this pass rest ONLY on level/unquantified facts (trajectory basis LevelOnly under "
                    + "{ClassifierVersion}); each is persisted verbatim and materializes no signal. "
                    + "{ReferenceSupported} rest on a level beside a cited company-reported reference value "
                    + "(ReferenceSupported under {ProjectionVersion}) and materialize normally.",
                cohortKey,
                measured.LevelOnly,
                measured.Directional,
                StatementComparisonClassifier.Version,
                measured.ReferenceSupported,
                ReferenceValueProjector.Version);
        }

        // Spec 215 §2: one Information line per cohort that made at least one call — the share of called
        // judgments that were handed >= 1 reference value. A measured 0 says "0 of N" (the ledger is
        // heal-forward, so this reads 0 until companies' releases have been read post-215); a cohort that
        // made no call says nothing.
        foreach (var (cohortKey, measured) in referencesSuppliedByCohort.OrderBy(
            e => e.Key, StringComparer.Ordinal))
        {
            _logger.LogInformation(
                "News-judgment cohort {Cohort}: {WithReferences} of {Judgments} judgment(s) called this pass "
                    + "were handed at least one company-reported reference value ({ProjectionVersion}).",
                cohortKey,
                measured.WithReferences,
                measured.Judgments,
                ReferenceValueProjector.Version);
        }

        // SPEC 223 §3: beside the spec-215 line, one Information line per cohort that made at least one
        // call — the called judgments handed NO reference, split by WHY on separate axes, plus the accrued
        // LedgerEntriesOnDisk (rendered "not recorded (<reason>)" when it could not be established, never
        // 0). A cohort that made no call says nothing (the same convention as the line above); a cohort
        // whose every call was handed a reference says "0" on every axis, which is a measured zero.
        foreach (var (cohortKey, measured) in referencesSuppliedByCohort.OrderBy(
            e => e.Key, StringComparer.Ordinal))
        {
            var absence = noReferencesByCohort.GetValueOrDefault(cohortKey) ?? new ReferenceAbsenceCounters();
            _logger.LogInformation(
                "News-judgment cohort {Cohort}: JudgmentsWithNoReferencesAvailable {NoReferences} of "
                    + "{Judgments} called — LedgerEmpty {LedgerEmpty} (the ledger holds nothing at all) / "
                    + "NoMatchingReference {NoMatchingReference} (the ledger holds values, none for the "
                    + "metrics these facts name) / ExcludedByEligibility {ExcludedByEligibility} (records "
                    + "existed, all excluded as newest, later than the fact, or superseded policy) / "
                    + "NotRecorded {NotRecorded} (ledger not registered, unreadable, or inventory "
                    + "unavailable). LedgerEntriesOnDisk {LedgerEntriesOnDisk} ({ProjectionVersion}).",
                cohortKey,
                absence.Total,
                measured.Judgments,
                absence.LedgerEmpty,
                absence.NoMatchingReference,
                absence.ExcludedByEligibility,
                absence.NotRecorded,
                RenderLedgerEntriesOnDisk(ledgerInventory, _reportedMetrics is not null),
                ReferenceValueProjector.Version);
        }

        // SPEC 219 §4 — the RUN-LEVEL coverage line, emitted exactly ONCE by a pass that reaches here,
        // however many (judge × stage-1 cohort) pairs ran, because every fact on it is a property of the
        // PLAN and not of a cohort. A pass that returned at the stage-1 precondition above emits it ZERO
        // times — that branch logs its own Warning naming the reason, so the run is not silent, but this
        // line is not a per-run guarantee. A universe that was never read renders "not recorded", never a
        // fabricated 0; a valve that did not bite reports a measured 0 rather than staying silent.
        _logger.LogInformation(
            "News-judgment run coverage ({CoveragePolicy}): {InUniverse} company/companies in universe; "
                + "planned {Planned} candidate(s) = {Full} full + {Breadth} breadth; {DroppedByValve} "
                + "breadth-eligible company/companies dropped by the capacity valve {ValveName}={Valve}. "
                + "Per-cohort coverage is reported on its own line for each judge × stage-1 cohort.",
            NewsJudgmentCoveragePolicy.Version,
            plan.CompaniesInUniverse is { } inUniverse
                ? inUniverse.ToString(CultureInfo.InvariantCulture)
                : "not recorded",
            candidates.Count,
            plan.Candidates.Count,
            plan.BreadthCandidates.Count,
            plan.BreadthDroppedByCapacityValve,
            "Radar:NewsResearch:Judgment:MaxCompaniesPerRun",
            plan.CapacityValve is { } valve
                ? valve.ToString(CultureInfo.InvariantCulture)
                : "not recorded");

        var markers = BuildPresentationMarkers(
            judgments, typing, runId, candidates, unpersisted, skippedNoTypedFacts);

        _logger.LogInformation(
            "News-judgment pass complete: {Candidates} candidate(s) ({Full} full + {Breadth} breadth) × "
                + "{Judges} judge(s) × {Stage1Cohorts} stage-1 cohort(s) = {Judgments} judgment record(s); "
                + "presentation markers {MarkerState}.",
            candidates.Count,
            plan.Candidates.Count,
            plan.BreadthCandidates.Count,
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

    /// <summary>
    /// The PASS-LOCAL outcome of one <see cref="JudgeOneAsync"/> invocation (spec 188 §1): the record to
    /// persist/reuse/present, plus whether THIS invocation actually invoked
    /// <see cref="INewsJudgmentAnalyzer"/> and how long that one call took.
    /// <para>
    /// Transient orchestration state — never persisted, never a wire contract, never an identity input.
    /// It exists because <see cref="NewsJudgmentRecord.ProviderDurationMs"/> answers a DIFFERENT question:
    /// it is the durable provenance of the call that CREATED that attempt, which a same-run reuse or a
    /// completed-cache reuse correctly carries forward without spending a call. Only
    /// <see cref="ProviderCallDurationThisPass"/> may drive this pass's counters, latency samples,
    /// failure totals and progress boundary.
    /// </para>
    /// </summary>
    private readonly record struct JudgmentPassOutcome(
        NewsJudgmentRecord Record,
        TimeSpan? ProviderCallDurationThisPass,
        // Spec 219 §4: whether this invocation REPLAYED an existing verdict — the completed-judgment cache
        // or same-run idempotency — rather than deciding one. It is not the same question as
        // "did this pass make a call": InsufficientFacts and AttemptsExhausted also make no call, and
        // neither is a reuse. Transient orchestration state, never persisted.
        bool ReusedExistingVerdict = false,
        // Spec 223 §3: WHY the projection this call was made against handed zero references (null when it
        // handed at least one, and null on every no-call branch — a reused verdict's absence is the
        // ORIGINAL call's, not this pass's activity). Transient orchestration state, never persisted.
        ReferenceAbsenceReason? ReferenceAbsenceReason = null)
    {
        /// <summary>A no-call branch: the record stands, this pass spent nothing.</summary>
        public static JudgmentPassOutcome WithoutCall(NewsJudgmentRecord record) => new(record, null);

        /// <summary>A no-call branch that REPLAYED an existing verdict (cache hit or same-run reuse).</summary>
        public static JudgmentPassOutcome Reused(NewsJudgmentRecord record) =>
            new(record, null, ReusedExistingVerdict: true);
    }

    /// <summary>
    /// SPEC 223 §3 — the per-cohort split of called judgments handed NO reference. Four axes, never
    /// merged: <see cref="LedgerEmpty"/> (the inventory says the ledger holds nothing at all),
    /// <see cref="NoMatchingReference"/> (the ledger holds values, none for this company's named metrics),
    /// <see cref="ExcludedByEligibility"/> (records existed, all excluded by the spec-216 rules) and
    /// <see cref="NotRecorded"/> (the store is not registered, this company's ledger was unreadable, or the
    /// inventory could not be read — a not-recorded is never a measured zero).
    /// </summary>
    private sealed class ReferenceAbsenceCounters
    {
        public int LedgerEmpty { get; private set; }
        public int NoMatchingReference { get; private set; }
        public int ExcludedByEligibility { get; private set; }
        public int NotRecorded { get; private set; }

        public int Total => LedgerEmpty + NoMatchingReference + ExcludedByEligibility + NotRecorded;

        public void Observe(ReferenceAbsenceClass cls)
        {
            switch (cls)
            {
                case ReferenceAbsenceClass.LedgerEmpty:
                    LedgerEmpty++;
                    break;
                case ReferenceAbsenceClass.NoMatchingReference:
                    NoMatchingReference++;
                    break;
                case ReferenceAbsenceClass.ExcludedByEligibility:
                    ExcludedByEligibility++;
                    break;
                default:
                    NotRecorded++;
                    break;
            }
        }
    }

    /// <summary>The spec-223 §3 classification of one zero-reference called judgment.</summary>
    internal enum ReferenceAbsenceClass
    {
        NotRecorded = 0,
        LedgerEmpty = 1,
        NoMatchingReference = 2,
        ExcludedByEligibility = 3,
    }

    /// <summary>
    /// SPEC 223 §3 — classifies one zero-reference called judgment. NotRecorded wins whenever the answer
    /// cannot be established (no store, an unreadable company ledger, no inventory, or an inventory whose
    /// record count is itself not recorded); "ledger empty" is claimed ONLY from a MEASURED zero on the
    /// global inventory; otherwise the projection's own reason decides between "no matching reference"
    /// (the company's ledger is empty or names none of these metrics — the ledger has values elsewhere)
    /// and "excluded by eligibility". A projection with zero references and no stated reason is
    /// impossible by construction and is reported as not recorded rather than as a fabricated class.
    /// </summary>
    internal static ReferenceAbsenceClass ClassifyReferenceAbsence(
        ReferenceAbsenceReason? projectionReason,
        ReportedMetricLedgerInventory? inventory,
        bool ledgerRegistered,
        bool companyLedgerUnreadable)
    {
        if (!ledgerRegistered || companyLedgerUnreadable || inventory?.LedgerRecords is not { } recordsOnDisk)
        {
            return ReferenceAbsenceClass.NotRecorded;
        }

        if (recordsOnDisk == 0)
        {
            return ReferenceAbsenceClass.LedgerEmpty;
        }

        return projectionReason switch
        {
            ReferenceAbsenceReason.CompanyLedgerEmpty => ReferenceAbsenceClass.NoMatchingReference,
            ReferenceAbsenceReason.NoRecordForNamedMetrics => ReferenceAbsenceClass.NoMatchingReference,
            ReferenceAbsenceReason.AllExcludedByEligibility => ReferenceAbsenceClass.ExcludedByEligibility,
            _ => ReferenceAbsenceClass.NotRecorded,
        };
    }

    /// <summary>
    /// SPEC 223 §3 — renders <c>LedgerEntriesOnDisk</c> for the per-cohort line: the measured record count
    /// (an absent root directory is the measured zero and says so), or <c>not recorded (&lt;reason&gt;)</c>
    /// — never a defaulted 0.
    /// </summary>
    internal static string RenderLedgerEntriesOnDisk(ReportedMetricLedgerInventory? inventory, bool ledgerRegistered)
    {
        // The not-recorded branches are the record's own (shared with CollectionPass's ledger line, so the
        // two renderings cannot drift); only the measured record count is this line's.
        if (ReportedMetricLedgerInventory.DescribeNotRecorded(inventory, ledgerRegistered) is { } notRecorded)
        {
            return notRecorded;
        }

        // DescribeNotRecorded returned null, so the ledger is registered, the inventory is present and the
        // counts are recorded.
        var records = inventory!.RecordedCounts.Records;
        return inventory.RootDirectoryExists == false
            ? string.Create(CultureInfo.InvariantCulture, $"{records} (ledger root directory absent)")
            : records.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// SPEC 223 §3 — reads the global ledger inventory ONCE per pass. Null when no ledger is registered or
    /// the read fails (the failure is logged and every zero-reference judgment then classifies as
    /// NotRecorded); only cancellation propagates.
    /// </summary>
    private async Task<ReportedMetricLedgerInventory?> InventoryLedgerAsync(CancellationToken ct)
    {
        if (_reportedMetrics is null)
        {
            return null;
        }

        try
        {
            return await _reportedMetrics.InventoryAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Reported-metrics ledger inventory could not be read this pass; every judgment handed no "
                    + "reference value is classified as NotRecorded rather than as a ledger that is empty.");
            return null;
        }
    }

    /// <summary>
    /// Spec 215 §2 — reads each candidate's reported-metrics ledger ONCE per pass. A read failure for one
    /// company degrades to an empty ledger for that company (its judgments are assembled with zero
    /// references, byte-identical to the pre-215 input) and is reported in ONE Warning per company —
    /// never silently, and never a failure of the pass. No registered ledger => an empty map, no read.
    /// Spec 223 §3: each unreadable company is added to <paramref name="unreadableLedgers"/> so its
    /// zero-reference judgments classify as NotRecorded, never as a measured empty.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ReportedMetricRecord>>> LoadLedgersAsync(
        IReadOnlyList<NewsRiskCandidate> candidates, ISet<Guid> unreadableLedgers, CancellationToken ct)
    {
        var ledgers = new Dictionary<Guid, IReadOnlyList<ReportedMetricRecord>>();
        if (_reportedMetrics is null)
        {
            return ledgers;
        }

        var unreadable = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (ledgers.ContainsKey(candidate.CompanyId))
            {
                continue;
            }

            try
            {
                ledgers[candidate.CompanyId] = await _reportedMetrics
                    .GetForCompanyAsync(candidate.CompanyId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                unreadable++;
                unreadableLedgers.Add(candidate.CompanyId);
                ledgers[candidate.CompanyId] = [];
                _logger.LogWarning(
                    ex,
                    "Reported-metrics ledger for company {Company} ({CompanyId}) could not be read; its "
                        + "judgments this pass are assembled with NO reference values.",
                    candidate.CompanyName,
                    candidate.CompanyId);
            }
        }

        if (unreadable > 0)
        {
            _logger.LogWarning(
                "Reported-metrics ledger: {Unreadable} of {Companies} candidate company ledger(s) could not "
                    + "be read this pass (each named above); those judgments carry no reference values.",
                unreadable,
                ledgers.Count);
        }

        return ledgers;
    }

    /// <summary>
    /// Judges one planned candidate. Returns <c>null</c> for exactly ONE condition — a
    /// <see cref="NewsJudgmentReadDepth.Breadth"/> candidate this stage-1 cohort holds no families for —
    /// which the caller counts as <c>CompaniesSkippedNoTypedFacts</c> and persists nothing for (spec 219
    /// §1/§4). Every other outcome, including a DEPTH candidate with zero families, returns a record.
    /// </summary>
    private async Task<JudgmentPassOutcome?> JudgeOneAsync(
        NewsJudgmentReader judge,
        NewsTypingCohortRunResult cohort,
        NewsJudgmentPlannedCandidate planned,
        Guid? runId,
        NewsObservationBatch? batch,
        JudgmentAttemptHistory history,
        IReadOnlyList<ReportedMetricRecord> ledger,
        CancellationToken ct)
    {
        var candidate = planned.Candidate;
        var cohortKey = judge.Identity.CohortKeyFor(cohort.Reader.CohortKey);
        // Spec 219 §2: DEPTH is what the budget caps. A breadth candidate is read at the bounded
        // MaxFamiliesPerBreadthJudgment; a depth candidate keeps the full MaxFamiliesPerJudgment. Same
        // assembly code, same deterministic family order — only the bound differs.
        var maxFamilies = _options.MaxFamiliesFor(planned.Depth);
        // Spec 215 §2: the company's ledger is projected against the SUPPLIED statements inside the builder,
        // so the references (and the family-set hash they fold into when non-empty) are decided in one place.
        var bundle = NewsJudgmentInputBuilder.Build(
            candidate.CompanyId, cohort.Families, cohort.FactsById, maxFamilies, ledger);
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
            planned,
            runId,
            bundle,
            coverage,
            typingCompleteness,
            attemptNumber: priorAttempts + 1);

        if (bundle.Families.Count == 0)
        {
            if (planned.Depth == NewsJudgmentReadDepth.Breadth)
            {
                // Spec 219 §1: a breadth company with nothing to read is SKIPPED AND COUNTED by the caller,
                // not recorded. See the caller for why ~80 non-attempts a run is noise rather than
                // provenance — the counter is the honest measure of what the judge could not reach.
                return null;
            }

            // Zero canonical families ⇒ a recorded InsufficientFacts attempt, never a model call and never
            // a "no challenge" (spec 185 §5). UNCHANGED for the depth cohort by spec 219.
            return JudgmentPassOutcome.WithoutCall(
                baseRecord with { Status = NewsJudgmentStatus.InsufficientFacts });
        }

        var cached = await _store
            .FindCompletedAsync(cohortKey, candidate.CompanyId, bundle.FamilySetHash, ct)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            // The cache carries ONLY the verdict fields; every completeness dimension comes from BaseRecord
            // and is therefore always the CURRENT run's (the spec-182 rule: a cached verdict replayed under
            // different coverage circumstances never carries a stale derived state) — including the spec-219
            // ReadDepth and family accounting, which describe THIS run's assembly, not the original call's.
            return JudgmentPassOutcome.Reused(baseRecord with
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
                // Spec 192 §2: the replayed verdict's OWN rationale facts travel with it. Leaving them at
                // the BaseRecord default would make a reused judgment read as "not recorded" beside the
                // very rationale it carries forward.
                RationaleLength = cached.RationaleLength,
                RationaleOverSoftLimit = cached.RationaleOverSoftLimit,
                // Spec 197 §2.2: the replayed verdict keeps its ORIGINAL durable expansion count — it is
                // truthful provenance of the call that produced it. It is NOT reported as a current-pass
                // normalization: the aggregate below counts only judgments this pass actually called for.
                FactIdPrefixExpansionCount = cached.FactIdPrefixExpansionCount,
                // Spec 214 §2: the replayed verdict's OWN basis travels with it — it is provenance of the
                // call that produced the verdict, and the materializer must gate the reuse exactly as it
                // gated the original. Never re-derived from this run's families.
                TrajectoryBasis = cached.TrajectoryBasis,
                // Spec 215 §2: the replayed verdict's OWN reference provenance travels with it — the set
                // the judge was handed and the ids it cited when the verdict was made. The cache key
                // already folds the projected ids, so a grown ledger never reaches this branch.
                ReferenceIds = cached.ReferenceIds,
                ReferenceValuesOmitted = cached.ReferenceValuesOmitted,
                TrajectoryReferenceIds = cached.TrajectoryReferenceIds,
                // Spec 216 §5: the kinds and the policy travel with the replayed verdict for the same
                // reason — they describe the projection the ORIGINAL call was made against.
                ReferencePolicy = cached.ReferencePolicy,
                ReferenceKinds = cached.ReferenceKinds,
                TrajectoryReferenceKinds = cached.TrajectoryReferenceKinds,
                ReusedFromJudgmentId = cached.JudgmentId,
            });
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

            // Spec 188 §1: the reused record keeps its ORIGINAL non-null ProviderDurationMs — it is
            // truthful provenance of the call that created it, and the in-memory copy must not disagree
            // with the insert-only record on disk. This pass simply made no call.
            return JudgmentPassOutcome.Reused(sameRun);
        }

        if (priorAttempts >= _options.MaxJudgmentAttempts)
        {
            // The bound: NO call, and a same-run record that SAYS so. It is not a completed judgment, it
            // does not itself count as an attempt, and it carries no model result.
            return JudgmentPassOutcome.WithoutCall(baseRecord with
            {
                JudgmentId = NewsJudgmentRecord.ExhaustionIdentityFor(
                    cohortKey, candidate.CompanyId, bundle.FamilySetHash, runId),
                Status = NewsJudgmentStatus.AttemptsExhausted,
                FailureDetail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"attempts-exhausted: {priorAttempts} call-producing judgment attempt(s) have already "
                        + $"been recorded for this cohort/company/family set, reaching the "
                        + $"{_options.MaxJudgmentAttempts}-attempt bound; no model call was made."),
            });
        }

        // The model request carries the company name/ticker and the canonical families ONLY (spec 185 §1):
        // no raw prose, no score, rank or label, no price, no prior judgment.
        var request = new NewsJudgmentAnalysisRequest(
            candidate.CompanyName, candidate.Ticker, bundle.Families, bundle.References);

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

        // Spec 188 §1: ONE measurement, written into BOTH the durable record (provenance of this call) and
        // the pass-local outcome (this pass's activity), so the two can never disagree.
        var callDuration = _timeProvider.GetElapsedTime(callStartTimestamp);
        var record = baseRecord with
        {
            RawResponseHash = outcome.RawResponseHash,
            ProviderDurationMs = callDuration.TotalMilliseconds,
        };
        switch (outcome.Failure)
        {
            case NewsJudgmentAnalysisFailure.ProviderError:
                return new JudgmentPassOutcome(
                    record with
                    {
                        Status = NewsJudgmentStatus.ProviderFailure,
                        FailureDetail = outcome.FailureDetail,
                    },
                    callDuration,
                    ReferenceAbsenceReason: bundle.ReferenceAbsenceReason);
            case NewsJudgmentAnalysisFailure.ParseError:
                return new JudgmentPassOutcome(
                    record with
                    {
                        Status = NewsJudgmentStatus.ParseFailure,
                        FailureDetail = outcome.FailureDetail,
                    },
                    callDuration,
                    ReferenceAbsenceReason: bundle.ReferenceAbsenceReason);
            default:
            {
                var validated = NewsJudgmentValidator.Validate(
                    outcome.Response!, bundle.Families, bundle.References);
                return new JudgmentPassOutcome(
                    record with
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
                        // Spec 192 §2: the validator MEASURED these, so they are recorded rather than left
                        // null. Only an attempt that never produced a validated response (provider/parse
                        // failure, or no call at all) leaves them "not recorded".
                        RationaleLength = validated.RationaleLength,
                        RationaleOverSoftLimit = validated.RationaleOverSoftLimit,
                        // Spec 197 §2.2: MEASURED whenever a response was examined — including a response
                        // that then failed for an unrelated reason — so 0 means "every accepted citation
                        // was already complete", never "not recorded".
                        FactIdPrefixExpansionCount = validated.FactIdPrefixExpansionCount,
                        // Spec 214 §2: computed by the validator only for a directional Judged result;
                        // null = not applicable (Mixed/Unknown/failure), never a defaulted Supported.
                        TrajectoryBasis = validated.TrajectoryBasis,
                        // Spec 215 §2: the reference ids the judge CITED for the trajectory (empty on a
                        // failure or when none was cited); the projected set and the omitted count are
                        // already on the base record.
                        TrajectoryReferenceIds = validated.TrajectoryReferenceIds,
                        // Spec 216 §5: and their KINDS, resolved from the very projection the judge was
                        // handed — never re-derived from a ledger that may since have grown.
                        TrajectoryReferenceKinds = ReferenceKindsFor(
                            validated.TrajectoryReferenceIds, bundle.References),
                    },
                    callDuration,
                    // Spec 223 §3: the projection's reason for an empty reference set, threaded in-process
                    // to the pass counters; null when at least one reference was handed.
                    ReferenceAbsenceReason: bundle.ReferenceAbsenceReason);
            }
        }
    }

    /// <summary>
    /// SPEC 216 §5 — pairs each CITED reference id with its KIND, looked up in the projection the judge was
    /// actually handed. An id with no match is impossible by construction (the validator resolves every
    /// citation against that same set) and is omitted rather than given a fabricated kind.
    /// </summary>
    private static IReadOnlyList<NewsJudgmentReferenceRef> ReferenceKindsFor(
        IReadOnlyList<Guid> citedIds, IReadOnlyList<NewsJudgmentReferenceValue> projected)
    {
        var kindById = projected.ToDictionary(r => r.ReferenceId, r => r.Kind);
        return
        [
            .. citedIds
                .Where(kindById.ContainsKey)
                .Select(id => new NewsJudgmentReferenceRef(id, kindById[id])),
        ];
    }

    private NewsJudgmentRecord BaseRecord(
        NewsJudgmentReaderIdentity identity,
        string cohortKey,
        NewsTypingCohortRunResult cohort,
        NewsJudgmentPlannedCandidate planned,
        Guid? runId,
        NewsJudgmentInputBundle bundle,
        NewsRiskCoverageEvaluation coverage,
        NewsTypingCompleteness typingCompleteness,
        int attemptNumber)
    {
        var candidate = planned.Candidate;
        return new(
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
                f.FamilyId,
                f.RepresentativeFactId,
                f.MemberCount,
                f.DistinctPublisherCount,
                // Spec 214 §1: the basis exactly as it was rendered to the judge for this family.
                f.ComparisonBasis,
                // Spec 216 §1: the observation instant the reference-eligibility guard read.
                f.ObservedAtUtc))
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
        // Spec 219 §2: the bound that ACTUALLY cut this attempt's input, resolved from the SAME
        // NewsJudgmentOptions.MaxFamiliesFor the builder above was given — so the record can never state a
        // bound the assembly did not run under.
        Limits: _options.ToLimitsRecord(_options.MaxFamiliesFor(planned.Depth)),
        ReusedFromJudgmentId: null,
        CreatedAtUtc: _timeProvider.GetUtcNow(),
        // Spec 215 §2: the PROJECTED reference set (ids only) and the caps' counted remainder, recorded on
        // every attempt that assembled an input — including InsufficientFacts and failures — so "which
        // references was this judge handed" is answerable from the record alone.
        ReferenceIds: bundle.References.Select(r => r.ReferenceId).ToList(),
        ReferenceValuesOmitted: bundle.ReferenceValuesOmitted,
        // Spec 216 §5: the policy the projection read under, the KIND of every projected reference, and
        // the projection's counted exclusions — recorded on every attempt that assembled an input, so a
        // judgment handed NO reference can say WHY rather than being indistinguishable from a company with
        // no ledger at all.
        ReferencePolicy: ReportedMetricsPolicy.Version,
        ReferenceKinds: bundle.References
            .Select(r => new NewsJudgmentReferenceRef(r.ReferenceId, r.Kind))
            .ToList(),
        ReferencesExcludedNewest: bundle.ReferencesExcludedNewest,
        ReferencesExcludedLaterThanFact: bundle.ReferencesExcludedLaterThanFact,
        ReferencesSkippedSupersededPolicy: bundle.ReferencesSkippedSupersededPolicy,
        // SPEC 219 §2: the coverage facts of THIS assembly — which budget read the company, how many
        // families were resolvable before the cut, and how many the cut left out. Recorded on every attempt
        // that assembled an input (a failure included), so "was this a bounded read" is answerable from the
        // record alone and can never be inferred wrongly from a supplied count that happens to be small.
        ReadDepth: planned.Depth,
        FamiliesAvailable: bundle.FamiliesAvailable,
        FamiliesWithheldByBudget: Math.Max(0, bundle.FamiliesAvailable - bundle.Families.Count),
        // SPEC 220 §3: the per-basis breakdown of FamiliesAvailable, from the SAME bundle — so it describes
        // this run's assembly (a cache reuse included) and can never disagree with the families beside it.
        FamiliesAvailableByBasis: bundle.FamiliesAvailableByBasis,
        // SPEC 221 §2a/§3: what was HANDED to the judge (derived from the supplied families only — never the
        // verdict's source) and what the business-first selection did, from the SAME bundle, so a cache reuse
        // describes this run's assembly exactly as the spec-219/220 fields do.
        SuppliedBasisProfile: NewsJudgmentSuppliedBasisProfile.Of(bundle.Families),
        FamiliesNonBusinessAvailable: bundle.FamiliesNonBusinessAvailable,
        FamiliesNonBusinessDemotedBySelection: bundle.FamiliesNonBusinessDemotedBySelection,
        FamiliesWithNoEventTypesAvailable: bundle.FamiliesWithNoEventTypesAvailable);
    }

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
        IReadOnlyList<NewsJudgmentPlannedCandidate> candidates,
        IReadOnlySet<(Guid CompanyId, string CohortKey)> unpersisted,
        IReadOnlySet<(Guid CompanyId, string CohortKey)> skippedNoTypedFacts)
    {
        // SPEC 194 §1.2: resolved through the SHARED NewsJudgmentPresentationCohort, which the
        // judgment-signal materializer also calls. One resolution, so the cohort whose direction is SCORED
        // and the cohort whose marker is DISPLAYED cannot drift apart.
        if (NewsJudgmentPresentationCohort.TryResolve(_options, _judges, typing) is not { } presentation)
        {
            _logger.LogError(
                "News-judgment presentation cohort (judge '{Judge}', extractor '{Extractor}') was not "
                    + "resolvable this run; the leaders markers stay unassessed rather than rendering from "
                    + "an undesignated cohort.",
                _options.PresentationJudge,
                _options.PresentationExtractor);
            return null;
        }

        var presentationCohortKey = presentation.CohortKey;
        // Spec 219 §1: the COMBINED cohort. Breadth companies now reach report rows too (the seeded
        // universe is inside Radar:ReportMaxItems), so a row that was `not-a-candidate` before this slice
        // now carries its own read — and, when that read was bounded, says so.
        var markers = new Dictionary<Guid, NewsJudgmentLeaderMarker>(candidates.Count);
        foreach (var planned in candidates)
        {
            var candidate = planned.Candidate;
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

            if (record is null
                && skippedNoTypedFacts.Contains((candidate.CompanyId, presentationCohortKey)))
            {
                // Spec 219 §1: a planned BREADTH company this cohort held no families for. The condition is
                // exactly the one an InsufficientFacts RECORD names, so it reuses that token rather than
                // minting a near-duplicate — what it must never say is `not-a-candidate`, which would be a
                // false claim about selection. No JudgmentId, because no attempt was recorded.
                markers[candidate.CompanyId] = new NewsJudgmentLeaderMarker(
                    NewsJudgmentMarkerState.Unassessed, NewsJudgmentMarkerReasons.InsufficientFacts);
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
    /// SPEC 219 §4 — the per-(judge × stage-1 cohort) coverage ledger behind the ONE aggregated line each
    /// pass emits. Every field is a MEASURED count over that pass: a zero is reported as a zero, and nothing
    /// here is ever inferred from a durable record's provenance (the spec-188 §1 rule).
    /// <para>
    /// Split by <see cref="NewsJudgmentReadDepth"/> wherever the spec asks for it, because "a breadth read
    /// fails validation more often than a full read" is a FINDING and must be visible without a re-run.
    /// </para>
    /// </summary>
    private sealed class JudgmentCoverageCounters
    {
        /// <summary>Breadth candidates this cohort held zero families for: skipped, counted, no record.</summary>
        public int SkippedNoTypedFacts { get; set; }

        /// <summary>Candidates whose input assembly found at least one resolvable family.</summary>
        public int WithTypedFacts { get; private set; }

        /// <summary>
        /// Candidates whose input assembly found ZERO resolvable families but which still produced a
        /// RECORD — in practice the DEPTH cohort's <see cref="NewsJudgmentStatus.InsufficientFacts"/> path,
        /// which spec 219 deliberately leaves unchanged (a leader row about to be shown to a human must be
        /// able to name why it was not assessed). Its own axis because without it the coverage line did not
        /// RECONCILE: such a candidate is in neither <see cref="WithTypedFacts"/> nor
        /// <see cref="SkippedNoTypedFacts"/>, so the line silently accounted for fewer candidates than it
        /// walked.
        /// </summary>
        public int RecordedWithNoTypedFacts { get; private set; }

        /// <summary>
        /// Candidates whose record did NOT RECORD its family accounting — a hydrated pre-219 record. Its own
        /// axis, never folded into the totals as a zero: a defaulted zero would under-count
        /// <see cref="FamiliesAvailable"/> while reading as a measured "no families", which is the
        /// defaulted-zero-as-measured-zero defect by name. Unreachable while every record this pass writes
        /// is a v8 one, and counted anyway so it can never become silent.
        /// </summary>
        public int FamiliesNotRecorded { get; private set; }

        /// <summary>
        /// Resolvable families across every assembled input that RECORDED the count, before the budget cut.
        /// The denominator is <c>WithTypedFacts + RecordedWithNoTypedFacts</c>, never the candidate total —
        /// <see cref="FamiliesNotRecorded"/> contributes to neither this nor
        /// <see cref="FamiliesWithheldByBudget"/>.
        /// </summary>
        public int FamiliesAvailable { get; private set; }

        /// <summary>Families actually handed to the judge.</summary>
        public int FamiliesSupplied { get; private set; }

        /// <summary>Families the budget left out — <see cref="FamiliesAvailable"/> minus <see cref="FamiliesSupplied"/>, accumulated per judgment so a per-company bite is never averaged away.</summary>
        public int FamiliesWithheldByBudget { get; private set; }

        public int ReusedBreadth { get; private set; }

        public int ReusedFull { get; private set; }

        public int FailedBreadth { get; private set; }

        public int FailedFull { get; private set; }

        public int ValidationFailedBreadth { get; private set; }

        public int ValidationFailedFull { get; private set; }

        /// <summary>Companies carrying a durably persisted <see cref="NewsJudgmentStatus.Judged"/> verdict this pass, at breadth depth.</summary>
        public int JudgedBreadth { get; private set; }

        /// <summary>The same at full depth.</summary>
        public int JudgedFull { get; private set; }

        /// <summary>
        /// Every candidate this pass ACCOUNTED FOR, on exactly one of the four mutually exclusive axes:
        /// <see cref="WithTypedFacts"/> + <see cref="RecordedWithNoTypedFacts"/> +
        /// <see cref="SkippedNoTypedFacts"/> + <see cref="FamiliesNotRecorded"/>. It must equal the number
        /// of planned candidates this pass walked, which is what makes the coverage line reconcile.
        /// </summary>
        public int Accounted =>
            WithTypedFacts + RecordedWithNoTypedFacts + SkippedNoTypedFacts + FamiliesNotRecorded;

        /// <summary>
        /// Records one judged candidate's assembly and outcome. Failures are counted ONLY when THIS pass
        /// actually called the provider (spec 188 §1): a replayed verdict legitimately carries the original
        /// call's failure status, and counting it here would report an old failure as a current one on
        /// exactly the rerun path this telemetry exists to explain.
        /// </summary>
        public void Observe(
            NewsJudgmentReadDepth depth, NewsJudgmentRecord record, JudgmentPassOutcome outcome)
        {
            FamiliesSupplied += record.Families.Count;
            // BaseRecord writes both from ONE bundle, so they are recorded together or not at all — and the
            // pattern ENFORCES that rather than asserting it: a record carrying one without the other is a
            // contradiction and lands wholesale on the not-recorded axis below. No `?? 0` anywhere here,
            // which is what keeps the partition four-way and the withheld total from silently absorbing a
            // fabricated zero.
            if (record is { FamiliesAvailable: { } available, FamiliesWithheldByBudget: { } withheld })
            {
                FamiliesAvailable += available;
                FamiliesWithheldByBudget += withheld;
                if (available > 0)
                {
                    WithTypedFacts++;
                }
                else
                {
                    RecordedWithNoTypedFacts++;
                }
            }
            else
            {
                // NOT RECORDED is its own answer. Adding 0 here would make a hydrated record read as a
                // measured "no families available" and quietly shrink the family totals.
                FamiliesNotRecorded++;
            }

            var breadth = depth == NewsJudgmentReadDepth.Breadth;
            if (outcome.ReusedExistingVerdict)
            {
                if (breadth)
                {
                    ReusedBreadth++;
                }
                else
                {
                    ReusedFull++;
                }
            }

            if (outcome.ProviderCallDurationThisPass is null)
            {
                return;
            }

            switch (record.Status)
            {
                case NewsJudgmentStatus.ValidationFailed:
                    if (breadth)
                    {
                        ValidationFailedBreadth++;
                        FailedBreadth++;
                    }
                    else
                    {
                        ValidationFailedFull++;
                        FailedFull++;
                    }

                    break;
                case NewsJudgmentStatus.ProviderFailure:
                case NewsJudgmentStatus.ParseFailure:
                    if (breadth)
                    {
                        FailedBreadth++;
                    }
                    else
                    {
                        FailedFull++;
                    }

                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// Records a DURABLY PERSISTED record. Only a <see cref="NewsJudgmentStatus.Judged"/> verdict counts
        /// as a company judged — an InsufficientFacts, exhausted or failed attempt is persisted provenance,
        /// not a read of the company's news.
        /// </summary>
        public void Persisted(NewsJudgmentReadDepth depth, NewsJudgmentRecord record)
        {
            if (record.Status != NewsJudgmentStatus.Judged)
            {
                return;
            }

            if (depth == NewsJudgmentReadDepth.Breadth)
            {
                JudgedBreadth++;
            }
            else
            {
                JudgedFull++;
            }
        }
    }

    /// <summary>
    /// SPEC 220 §3 — the per-(judge × stage-1 cohort) COMPARISON-BASIS ledger behind the ONE aggregated basis
    /// line each pass emits BESIDE (never instead of) the spec-219 coverage line. It answers "did the
    /// basis-first ordering change what the judge saw": families available, supplied and withheld by budget,
    /// each per basis class and split breadth / full, plus how many judgments this pass accepted with a
    /// challenge strength the model did not state (§2).
    /// <para>
    /// The family counts cover EVERY assembled input whose record RECORDED its per-basis accounting — reused
    /// verdicts included, because the families describe THIS run's assembly (the spec-219 counters' rule). A
    /// record that did not record it (a hydrated pre-220 record, or a pre-214 family ref with no basis) lands
    /// on <see cref="NotRecorded"/> and contributes nothing — never a `?? 0` that would read as a measured
    /// zero. <see cref="ChallengeStrengthNotStatedBreadth"/> / <see cref="ChallengeStrengthNotStatedFull"/>
    /// count ONLY judgments this pass actually CALLED the provider for (spec 188 §1: a replayed verdict is not
    /// current activity).
    /// </para>
    /// </summary>
    private sealed class JudgmentBasisCounters
    {
        public NewsJudgmentBasisCounts AvailableBreadth { get; private set; } = NewsJudgmentBasisCounts.Zero;

        public NewsJudgmentBasisCounts AvailableFull { get; private set; } = NewsJudgmentBasisCounts.Zero;

        public NewsJudgmentBasisCounts SuppliedBreadth { get; private set; } = NewsJudgmentBasisCounts.Zero;

        public NewsJudgmentBasisCounts SuppliedFull { get; private set; } = NewsJudgmentBasisCounts.Zero;

        /// <summary>Available minus supplied, per class, accumulated per judgment so a per-company bite is never averaged away.</summary>
        public NewsJudgmentBasisCounts WithheldBreadth { get; private set; } = NewsJudgmentBasisCounts.Zero;

        public NewsJudgmentBasisCounts WithheldFull { get; private set; } = NewsJudgmentBasisCounts.Zero;

        /// <summary>Assembled inputs whose record did not record a per-basis accounting — its own axis, never a zero.</summary>
        public int NotRecorded { get; private set; }

        public int ChallengeStrengthNotStatedBreadth { get; private set; }

        public int ChallengeStrengthNotStatedFull { get; private set; }

        public void Observe(
            NewsJudgmentReadDepth depth, NewsJudgmentRecord record, JudgmentPassOutcome outcome)
        {
            var breadth = depth == NewsJudgmentReadDepth.Breadth;

            // Recorded wholesale or not at all: the available breakdown AND every supplied family's basis.
            if (record.FamiliesAvailableByBasis is { } available
                && record.Families.All(f => f.ComparisonBasis is not null))
            {
                var supplied = NewsJudgmentBasisCounts.Of(record.Families.Select(f => f.ComparisonBasis!.Value));
                var withheld = available.Minus(supplied);
                if (breadth)
                {
                    AvailableBreadth = AvailableBreadth.Plus(available);
                    SuppliedBreadth = SuppliedBreadth.Plus(supplied);
                    WithheldBreadth = WithheldBreadth.Plus(withheld);
                }
                else
                {
                    AvailableFull = AvailableFull.Plus(available);
                    SuppliedFull = SuppliedFull.Plus(supplied);
                    WithheldFull = WithheldFull.Plus(withheld);
                }
            }
            else
            {
                NotRecorded++;
            }

            // The §2 condition has ONE definition (NewsJudgmentRecord.IsChallengeStrengthNotStated), read here
            // from the record the validator produced for a call made this pass.
            if (outcome.ProviderCallDurationThisPass is not null
                && NewsJudgmentRecord.IsChallengeStrengthNotStated(
                    record.Status, record.FindingsAccepted, record.ChallengeStrength))
            {
                if (breadth)
                {
                    ChallengeStrengthNotStatedBreadth++;
                }
                else
                {
                    ChallengeStrengthNotStatedFull++;
                }
            }
        }
    }

    /// <summary>
    /// SPEC 221 §3 — the per-(judge × stage-1 cohort) BUSINESS-SIGNAL ledger behind the two aggregated lines
    /// each pass emits beside the spec-219 coverage and spec-220 basis lines.
    /// <list type="bullet">
    /// <item><b>Family counts</b> (<see cref="Observe"/>) cover EVERY assembled input whose record recorded its
    /// non-business accounting — reused verdicts included, because the families describe THIS run's assembly.
    /// Recorded wholesale or not at all; otherwise <see cref="FamilyAccountingNotRecorded"/>.</item>
    /// <item><b>Verdict counts and the worklist</b> (<see cref="Persisted"/>) cover exactly the judged verdicts
    /// the coverage line counts — durably persisted, called or reused — so the lines reconcile and every named
    /// judgmentId dereferences into the store. The two profile-derived counts use the ONE definitions on
    /// <see cref="NewsJudgmentRecord"/>; a verdict without a profile lands on <see cref="ProfileNotRecorded"/>.</item>
    /// </list>
    /// </summary>
    private sealed class JudgmentBusinessSignalCounters
    {
        public int NonBusinessAvailableBreadth { get; private set; }

        public int NonBusinessAvailableFull { get; private set; }

        public int NonBusinessSuppliedBreadth { get; private set; }

        public int NonBusinessSuppliedFull { get; private set; }

        public int DemotedBreadth { get; private set; }

        public int DemotedFull { get; private set; }

        public int NoEventTypesBreadth { get; private set; }

        public int NoEventTypesFull { get; private set; }

        public int FamilyAccountingNotRecorded { get; private set; }

        public int VerdictsBreadth { get; private set; }

        public int VerdictsFull { get; private set; }

        public int NoDirectionalBasisBreadth { get; private set; }

        public int NoDirectionalBasisFull { get; private set; }

        public int NoBusinessSignalBreadth { get; private set; }

        public int NoBusinessSignalFull { get; private set; }

        public int DisagreementBreadth { get; private set; }

        public int DisagreementFull { get; private set; }

        public int DeterioratingBreadth { get; private set; }

        public int DeterioratingFull { get; private set; }

        public int UnknownBreadth => UnknownWorklistBreadth.Count;

        public int UnknownFull => UnknownWorklistFull.Count;

        /// <summary>Judged verdicts whose supplied-basis profile was not recorded — its own axis, never a zero.</summary>
        public int ProfileNotRecorded { get; private set; }

        public List<(string Label, Guid JudgmentId)> UnknownWorklistBreadth { get; } = [];

        public List<(string Label, Guid JudgmentId)> UnknownWorklistFull { get; } = [];

        public void Observe(NewsJudgmentReadDepth depth, NewsJudgmentRecord record)
        {
            if (record is not
                {
                    SuppliedBasisProfile: { } profile,
                    FamiliesNonBusinessAvailable: { } available,
                    FamiliesNonBusinessDemotedBySelection: { } demoted,
                    FamiliesWithNoEventTypesAvailable: { } noTypes,
                })
            {
                FamilyAccountingNotRecorded++;
                return;
            }

            if (depth == NewsJudgmentReadDepth.Breadth)
            {
                NonBusinessAvailableBreadth += available;
                NonBusinessSuppliedBreadth += profile.NonBusiness.Sum();
                DemotedBreadth += demoted;
                NoEventTypesBreadth += noTypes;
            }
            else
            {
                NonBusinessAvailableFull += available;
                NonBusinessSuppliedFull += profile.NonBusiness.Sum();
                DemotedFull += demoted;
                NoEventTypesFull += noTypes;
            }
        }

        public void Persisted(NewsJudgmentReadDepth depth, NewsJudgmentRecord record)
        {
            if (record.Status != NewsJudgmentStatus.Judged)
            {
                return;
            }

            var breadth = depth == NewsJudgmentReadDepth.Breadth;
            if (breadth)
            {
                VerdictsBreadth++;
            }
            else
            {
                VerdictsFull++;
            }

            switch (record.BusinessTrajectory)
            {
                case NewsJudgmentTrajectory.NoBusinessSignal:
                    if (breadth)
                    {
                        NoBusinessSignalBreadth++;
                    }
                    else
                    {
                        NoBusinessSignalFull++;
                    }

                    break;
                case NewsJudgmentTrajectory.Deteriorating:
                    if (breadth)
                    {
                        DeterioratingBreadth++;
                    }
                    else
                    {
                        DeterioratingFull++;
                    }

                    break;
                case NewsJudgmentTrajectory.Unknown:
                    (breadth ? UnknownWorklistBreadth : UnknownWorklistFull)
                        .Add((record.Ticker ?? record.CompanyName, record.JudgmentId));
                    break;
                default:
                    break;
            }

            if (record.SuppliedBasisProfile is not { } profile)
            {
                ProfileNotRecorded++;
                return;
            }

            if (NewsJudgmentRecord.NoDirectionalBasisSupplied(profile))
            {
                if (breadth)
                {
                    NoDirectionalBasisBreadth++;
                }
                else
                {
                    NoDirectionalBasisFull++;
                }
            }

            if (NewsJudgmentRecord.ClassifierSaidDirectionalJudgeSaidNoBusinessSignal(
                record.Status, record.BusinessTrajectory, profile))
            {
                if (breadth)
                {
                    DisagreementBreadth++;
                }
                else
                {
                    DisagreementFull++;
                }
            }
        }

        /// <summary>
        /// The worklist rendering: <c>TICKER (judgmentId)</c> entries, ordinal by label then id (AD-3), comma
        /// joined — or the explicit word <c>none</c>, so an empty worklist is a stated fact, not a blank.
        /// </summary>
        public static string Describe(IReadOnlyList<(string Label, Guid JudgmentId)> worklist) =>
            worklist.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    worklist
                        .OrderBy(e => e.Label, StringComparer.Ordinal)
                        .ThenBy(e => e.JudgmentId)
                        .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Label} ({e.JudgmentId:D})")));
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
