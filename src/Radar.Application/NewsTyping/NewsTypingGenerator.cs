using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Ai;
using Radar.Application.News;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Pipeline;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The in-process news-typing step (spec 181 §4), invoked by the Worker AFTER the pipeline (and after the
/// spec-179 news-risk shadow). Read-side and shadow: no score, label, strategy, fingerprint, snapshot field
/// or report rank changes, and nothing here is hashed into any fingerprint.
/// </summary>
public interface INewsTypingGenerator
{
    /// <summary>
    /// Runs one typing pass for the completed run <paramref name="runId"/> (nullable — typing works over the
    /// archive and degrades to "no run provenance" rather than refusing). Never throws for its own failures
    /// (a typing failure writes the NAMED failed artifact and never rolls back or relabels the
    /// already-durable Radar run); caller cancellation propagates.
    /// <para>
    /// Returns the typed pass outcome (spec 185 §5) — the SAME families/facts this pass checkpointed, per
    /// cohort, for the stage-2 judge — or <c>null</c> when the pass failed. The return is additive
    /// observation: nothing about the pass itself changed.
    /// </para>
    /// <para>
    /// <paramref name="candidatePlan"/> (spec 187 §2) is the run's ordered judgment-candidate plan — the
    /// EXACT immutable instance the stage-2 judge then consumes, computed once by the Worker. It changes
    /// SELECTION ORDER ONLY. <c>null</c> (judgment disabled, or no candidate plan) leaves selection
    /// byte-identical to the pre-187 §2 behaviour, so it is trailing-optional rather than required.
    /// </para>
    /// </summary>
    Task<NewsTypingRunResult?> GenerateAsync(
        Guid? runId, CancellationToken ct, NewsJudgmentCandidatePlan? candidatePlan = null);
}

/// <summary>
/// Orchestrates one typing pass (spec 181): bounded per-reader selection over the spec-177 archive (this
/// run's window observations first — newest first — then backlog, OLDEST first) → one extractor call per
/// observation per reader (the extractor works ONE observation at a time) → mechanical validation → durable
/// per-attempt persistence with the completed-typing cache → the deterministic fact-family checkpoint per
/// cohort → the attention-decomposition artifact. Rules:
/// <list type="bullet">
/// <item><b>Cohorts never pool.</b> Each reader types independently under its own cohort key; capture-mode
/// cohorts stay separate in every output; no merged verdict exists anywhere.</item>
/// <item><b>Nothing is ever fetched.</b> Supplied text is the archived headline + description + stored
/// permitted body; the typing pass issues no HTTP request.</item>
/// <item><b>The BOUNDED BACKLOG PHASE is the catch-up mechanism this slice ships</b> (spec 181 §6): the 13k
/// legacy articles drain under <see cref="NewsTypingOptions.MaxNewTypingsPerRun"/> per reader per run. A
/// dedicated standalone catch-up entry point (its own run mode/command) is DEFERRED — deliberately not a new
/// <c>RunMode</c> in this slice.</item>
/// <item><b>Retries are BOUNDED and FAIR (spec 186 §2), and every hosted call is RESERVED FIRST (spec 187
/// §3).</b> Before spec 187 the attempt count was DERIVED from the insert-only outcome records, which
/// cannot bound hosted CALLS: the call happens before the outcome write, so a crash, a cancellation or an
/// <see cref="INewsTypingStore.WriteAsync"/> returning <c>false</c> spent a call and advanced the count by
/// nothing. The durable pre-call <see cref="INewsTypingAttemptLedger"/> is now the SOLE authority for new
/// attempt occupancy; the derived counter survives ONLY as the legacy-occupancy migration read for pre-187
/// outcome records that carry no <c>AttemptReservationId</c>. An observation that has spent
/// <see cref="NewsTypingOptions.MaxTypingAttempts"/> occupied attempts LEAVES selection (counted, warned once
/// per cohort, and its company's typing completeness degrades). Retries occupy their own bounded FIFO lane
/// (<see cref="NewsTypingOptions.MaxRetryTypingsPerRun"/>, oldest last-attempt first) inside the per-run cap,
/// so neither the backlog nor the retry queue can starve the other.</item>
/// <item><b>The CALL PROTOCOL is exact and ordered (spec 187 §3)</b>, and see
/// <c>RunReaderPassAsync</c> for the enforcement: (1) a completed-cache hit reserves nothing and calls
/// nothing; (2) occupancy at the cap is exhausted — no reservation, no call; (3) the next ordinal is claimed
/// ATOMICALLY; (4) only the winner invokes the provider; (5) the outcome is persisted LINKED to that
/// reservation; (6) only a successful <c>WriteAsync == true</c> may enter the completed map, count as a new
/// typing, contribute facts/families, or flow into the stage-2 judge. A reservation with no outcome
/// conservatively consumes an attempt and is surfaced as <c>ReservedWithoutOutcome</c> — the budget can be
/// spent early, never overspent, and unpersisted facts are never presented as durable evidence.</item>
/// <item><b>EXHAUSTION IS IMMEDIATE AND DISJOINT FROM BACKLOG (spec 187 §4).</b> The exhausted set is
/// updated DURING the pass, so a failure on the final permitted attempt is reported in the SAME run rather
/// than discovered by the next one; and an exhausted observation is counted as exhausted INSTEAD of as
/// backlog, so <c>UntypedRemaining + RetryExhausted + completed outcomes</c> reconciles to the eligible
/// population.</item>
/// <item><b>Selection runs THREE LANES inside the ONE per-run call budget (spec 187 §2)</b>, in this
/// order: (1) the bounded RETRY lane, whose spec-186 global FIFO fairness is deliberately UNCHANGED — a
/// current leader must never be able to pin failing calls forever; (2) the bounded CANDIDATE first-attempt
/// lane, filled ROUND-ROBIN over the ordered judgment-candidate plan (each candidate offers its unattempted
/// in-window observations newest-first, then its own legacy backlog oldest-first), so one noisy company
/// cannot consume the lane before the others receive an observation; (3) the GENERAL first-attempt lane,
/// which takes every unused slot in the existing global order. An observation selected by an earlier lane
/// is INELIGIBLE for the later ones. The config boundary reserves at least one general slot
/// (<c>candidate + retry &lt; perRun</c>) so candidate priority can never stop the legacy backlog draining;
/// with no candidate plan the candidate lane selects nothing and the pass is byte-identical to spec
/// 186's.</item>
/// <item><b>Run membership is approximated by the window, honestly.</b> Archive records carry no batch/run
/// id (batch manifests hold counters, not observation ids), so "this run's new observations" is implemented
/// as "window observations, newest first" — a superset that still guarantees this run's fresh captures are
/// typed before backlog under the cap.</item>
/// </list>
/// AD-14 boundary: this type has NO price dependency and no score-store seam of any kind (asserted
/// structurally by the news-typing architecture guard test).
/// </summary>
public sealed class NewsTypingGenerator : INewsTypingGenerator
{
    /// <summary>How many recent run records are scanned to resolve the durable record for this run id.</summary>
    private const int RunLookupWindow = 50;

    /// <summary>
    /// Spec 187 §7: how many ATTEMPTED provider calls a reader pass makes between bounded progress lines.
    /// 25 against the 200-call default budget is eight lines per reader — enough to watch throughput and
    /// failures live, few enough that the log stays readable. The final partial batch is always emitted, so
    /// a pass that ends mid-batch still reports its last calls.
    /// </summary>
    private const int TypingProgressBatchSize = 25;

    private readonly IPipelineRunStore _runStore;
    private readonly INewsObservationArchive _observationArchive;
    private readonly INewsObservationBatchReader _batchReader;
    private readonly NewsTypingReaderSet _readers;
    private readonly INewsTypingStore _store;
    private readonly INewsTypingAttemptLedger _attemptLedger;
    private readonly IFactFamilySnapshotStore _familyStore;
    private readonly INewsTypingArtifactStore _artifactStore;
    private readonly NewsTypingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NewsTypingGenerator> _logger;

    public NewsTypingGenerator(
        IPipelineRunStore runStore,
        INewsObservationArchive observationArchive,
        INewsObservationBatchReader batchReader,
        NewsTypingReaderSet readers,
        INewsTypingStore store,
        INewsTypingAttemptLedger attemptLedger,
        IFactFamilySnapshotStore familyStore,
        INewsTypingArtifactStore artifactStore,
        NewsTypingOptions options,
        TimeProvider timeProvider,
        ILogger<NewsTypingGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(observationArchive);
        ArgumentNullException.ThrowIfNull(batchReader);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(attemptLedger);
        ArgumentNullException.ThrowIfNull(familyStore);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (readers.Readers.Count == 0)
        {
            throw new ArgumentException(
                "NewsTypingReaderSet must resolve at least one reader; the composition root registers the "
                    + "typing step only when one (ambient or configured) exists.",
                nameof(readers));
        }

        _runStore = runStore;
        _observationArchive = observationArchive;
        _batchReader = batchReader;
        _readers = readers;
        _store = store;
        _attemptLedger = attemptLedger;
        _familyStore = familyStore;
        _artifactStore = artifactStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<NewsTypingRunResult?> GenerateAsync(
        Guid? runId, CancellationToken ct, NewsJudgmentCandidatePlan? candidatePlan = null)
    {
        var fallbackDateToken = _timeProvider.GetUtcNow().UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            return await GenerateCoreAsync(runId, candidatePlan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A typing failure must never abort or relabel the already-durable Radar run: write the named
            // failed artifact (itself best-effort) and return null (no stage-1 outcome for the judge).
            _logger.LogError(ex, "News-typing pass failed; writing the named failed artifact.");
            await _artifactStore
                .WriteFailedAsync(fallbackDateToken, $"{ex.GetType().Name}: {ex.Message}", ct)
                .ConfigureAwait(false);
            return null;
        }
    }

    private async Task<NewsTypingRunResult> GenerateCoreAsync(
        Guid? runId, NewsJudgmentCandidatePlan? candidatePlan, CancellationToken ct)
    {
        // Spec 187 §7: the MONOTONIC stage anchor. Every "elapsed" number the progress lines report is
        // derived from it via TimeProvider.GetElapsedTime, never from subtracting two wall-clock readings
        // (a clock adjustment mid-pass would otherwise print a negative or absurd elapsed time).
        var stageStartTimestamp = _timeProvider.GetTimestamp();
        var now = _timeProvider.GetUtcNow();
        var runRecord = runId is { } id ? await FindRunRecordAsync(id, ct).ConfigureAwait(false) : null;
        var asOfUtc = runRecord?.CreatedAtUtc ?? now;
        var asOfDateToken = asOfUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var windowStartUtc = asOfUtc.AddDays(-_options.LookbackDays);

        // Capture provenance for THIS run, resolved fail-closed: no resolvable batch manifest means
        // UNKNOWN, which the artifact renders as unproven — never as a clean batch.
        bool? captureProven = null;

        // Spec 189 §3: the run's CAPTURE INFLOW, read from the SAME batch manifest, so "how much arrived"
        // sits beside "how much we typed". Null when there is no batch or it is unreadable — never a
        // timestamp-derived estimate, which would read as a measurement while being a guess.
        int? observationsCapturedThisRun = null;
        if (runRecord?.NewsObservationBatchId is { } batchId)
        {
            var batch = await _batchReader.GetBatchAsync(batchId, ct).ConfigureAwait(false);
            captureProven = batch is null ? null : batch.CaptureProven && batch.FullUniverse;
            observationsCapturedThisRun = batch?.ObservationsWritten;
        }

        var observations = await _observationArchive.GetAllAsync(ct).ConfigureAwait(false);
        var eligible = observations
            .Select(NewsTypingInputObservation.FromRecord)
            .Where(o => o.HasSuppliedText)
            .ToList();

        // One durable read each, indexed in memory: the completed-typing cache lookup and the spec-187 §3
        // pre-call attempt occupancy for EVERY reader without an O(observations × records) scan per reader.
        var allRecords = await _store.GetAllAsync(ct).ConfigureAwait(false);
        var allReservations = await _attemptLedger.GetAllAsync(ct).ConfigureAwait(false);

        var observationIndex = eligible.ToDictionary(o => (o.ObservationId, o.PayloadHash));
        var perReader = new List<ReaderPass>(_readers.Readers.Count);
        foreach (var reader in _readers.Readers)
        {
            ct.ThrowIfCancellationRequested();
            var pass = await RunReaderPassAsync(
                reader,
                runId,
                eligible,
                allRecords,
                allReservations,
                candidatePlan,
                windowStartUtc,
                asOfUtc,
                stageStartTimestamp,
                ct)
                .ConfigureAwait(false);
            pass.ObservationIndex = observationIndex;
            perReader.Add(pass);
        }

        // Checkpoint fact families per cohort (spec 181 §4 / 186 §4): segmentation over ALL validated facts
        // from COMPLETED typings — never only this run's new facts (which would miss duplicates typed by
        // earlier runs), and never only the window (which would churn the durable family id) — then the
        // window projection that the snapshot, the decomposition and the stage-2 judge consume.
        foreach (var pass in perReader)
        {
            ct.ThrowIfCancellationRequested();
            await WriteFamilyCheckpointAsync(pass, now, windowStartUtc, asOfUtc, ct).ConfigureAwait(false);
        }

        var document = BuildDecomposition(
            runId,
            perReader,
            eligible,
            windowStartUtc,
            asOfUtc,
            captureProven,
            runRecord?.NewsObservationBatchId,
            observationsCapturedThisRun,
            now);
        await _artifactStore
            .WriteDecompositionAsync(
                asOfDateToken, NewsTypingDecompositionRenderer.RenderMarkdown(document), document, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "News-typing pass complete: {Readers} reader(s), {NewTypings} new typing(s), window "
                + "{WindowStart:o} → {AsOf:o}.",
            perReader.Count,
            perReader.Sum(p => p.NewTypings),
            windowStartUtc,
            asOfUtc);

        // Spec 185 §5: expose the pass's own join (families + completed facts + per-company completeness)
        // for the stage-2 judge — the SAME instances checkpointed above, never a disk re-read.
        return new NewsTypingRunResult(
            RunId: runId,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: asOfUtc,
            NewsObservationBatchId: runRecord?.NewsObservationBatchId,
            Cohorts: perReader
                .Select(p => BuildCohortRunResult(p, windowStartUtc, asOfUtc))
                .ToList());
    }

    /// <summary>
    /// One cohort's typed pass outcome (spec 185 §5). The fact index covers COMPLETED <c>Typed</c> window
    /// typings (the same set the family checkpoint consumed); the per-company completeness map follows spec
    /// 189 §2's TOTAL, CONSERVATIVE precedence — exhaustion outranks a retryable failure this pass, which
    /// outranks a backlog, which outranks complete — and a company with zero in-window observations is
    /// vacuously <see cref="NewsTypingCompleteness.Complete"/> (it also has zero facts, so the judge records
    /// <c>InsufficientFacts</c> for it anyway).
    /// </summary>
    private static NewsTypingCohortRunResult BuildCohortRunResult(
        ReaderPass pass, DateTimeOffset windowStartUtc, DateTimeOffset asOfUtc)
    {
        var factsById = new Dictionary<Guid, NewsTypingFactRef>();
        var factsDropped = 0;
        foreach (var (observation, record) in pass.CompletedWithObservations())
        {
            if (observation.FirstObservedAtUtc <= windowStartUtc || observation.FirstObservedAtUtc > asOfUtc)
            {
                continue;
            }

            factsDropped += record.FactsDropped;
            foreach (var fact in record.Facts)
            {
                factsById.TryAdd(fact.FactId, new NewsTypingFactRef(
                    Fact: fact,
                    ObservationId: observation.ObservationId,
                    CompanyId: observation.CompanyId,
                    CaptureMode: observation.CaptureMode));
            }
        }

        var completeness = new Dictionary<Guid, NewsTypingCompleteness>();
        if (pass.ObservationIndex is not null)
        {
            foreach (var companyGroup in pass.ObservationIndex.Values
                .Where(o => o.CompanyId is not null
                    && o.FirstObservedAtUtc > windowStartUtc
                    && o.FirstObservedAtUtc <= asOfUtc)
                .GroupBy(o => o.CompanyId!.Value))
            {
                var untyped = companyGroup.Count(
                    o => !pass.Completed.ContainsKey((o.ObservationId, o.PayloadHash)));

                // Spec 189 §2's precedence, in order. EXHAUSTION FIRST: an exhausted untyped observation is
                // a PERMANENT hole, not a deferral, so the company can never read Complete — and "Backlog"
                // (deferred by the per-run cap) would be a false statement about it. A retryable failure
                // comes next: it degraded THIS pass's read, but the observation stays eligible, so calling it
                // a permanent hole would be just as false in the other direction. The pre-189 token `Failed`
                // conflated the two and is never computed here again.
                //
                // KNOWN, RECORDED LIMITATION (not fixed here — narrowing the failure set to the window is its
                // own decision). The two sets have different scopes on purpose: `FailedCompanyIds` is
                // PASS-WIDE, `ExhaustedCompanyIds` is WINDOW-scoped. So an OUT-OF-WINDOW observation that
                // spends its FINAL attempt in this pass calls BOTH marks, yet only the pass-wide one records
                // the company — and if that company also holds an in-window observation, its token here reads
                // RetryableFailure for an observation that is in fact exhausted. The impact is bounded to the
                // TOKEN: the artifact's per-company row uses `ReaderPass.IsRetryableFailure`, which excludes
                // exhausted keys, so the rendered "remains in the eligible backlog" count correctly shows 0,
                // and `NewsJudgmentMarkerPolicy` treats every non-Complete value identically.
                completeness[companyGroup.Key] = pass.ExhaustedCompanyIds.Contains(companyGroup.Key)
                    ? NewsTypingCompleteness.RetryExhausted
                    : pass.FailedCompanyIds.Contains(companyGroup.Key)
                        ? NewsTypingCompleteness.RetryableFailure
                        : untyped > 0
                            ? NewsTypingCompleteness.Backlog
                            : NewsTypingCompleteness.Complete;
            }
        }

        return new NewsTypingCohortRunResult(
            Reader: pass.Identity,
            Families: pass.Families,
            FactsById: factsById,
            TypingCompletenessByCompany: completeness,
            FactsDroppedInWindow: factsDropped,
            RetryExhausted: pass.ExhaustedKeys.Count,
            ReservedWithoutOutcome: pass.OrphanedReservations.Values.Sum(),
            CandidatePrioritySelected: pass.SelectedIn(NewsTypingSelectionLane.CandidatePriority),
            GeneralSelected: pass.SelectedIn(NewsTypingSelectionLane.General),
            RetrySelected: pass.SelectedIn(NewsTypingSelectionLane.Retry));
    }

    private async Task<ReaderPass> RunReaderPassAsync(
        NewsTypingReader reader,
        Guid? runId,
        IReadOnlyList<NewsTypingInputObservation> eligible,
        IReadOnlyList<NewsTypingRecord> allRecords,
        IReadOnlyList<NewsTypingAttemptReservation> allReservations,
        NewsJudgmentCandidatePlan? candidatePlan,
        DateTimeOffset windowStartUtc,
        DateTimeOffset asOfUtc,
        long stageStartTimestamp,
        CancellationToken ct)
    {
        var cohortKey = reader.Identity.CohortKey;
        var cohortRecords = allRecords
            .Where(r => string.Equals(r.CohortKey, cohortKey, StringComparison.Ordinal))
            .ToList();

        // Completed cache for this cohort keyed by (observation, payload) — deterministic winner: most
        // recent CreatedAtUtc, then lowest TypingId (the FindCompletedAsync rule, AD-3).
        var completed = cohortRecords
            .Where(r => r.IsCompletedTyping)
            .GroupBy(r => (r.ObservationId, r.PayloadHash))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.TypingId).First());

        var (occupancy, orphanedReservations) =
            BuildOccupancy(cohortKey, cohortRecords, allReservations, runId);

        var untyped = eligible
            .Where(o => !completed.ContainsKey((o.ObservationId, o.PayloadHash)))
            .ToList();

        // Split the untyped set into the spec-186 §2 tiers, over spec 187 §3's OCCUPANCY. Order matters:
        // EXHAUSTION is a durable property of the ledger/record set and is reported whether or not this run
        // already touched the observation, while the same-run skip is an idempotency rule about THIS
        // invocation.
        var exhaustedKeys = new HashSet<(Guid ObservationId, string PayloadHash)>();
        var exhaustedCompanyIds = new HashSet<Guid>();

        // Spec 187 §4: exhaustion is recorded through ONE local rule, used both by the pre-pass split and
        // DURING the pass — so a failure on the final permitted attempt exhausts the observation in the
        // SAME run rather than being discovered by the next one.
        void MarkExhausted(NewsTypingInputObservation observation)
        {
            exhaustedKeys.Add((observation.ObservationId, observation.PayloadHash));
            if (observation.CompanyId is { } exhaustedCompanyId
                && observation.FirstObservedAtUtc > windowStartUtc
                && observation.FirstObservedAtUtc <= asOfUtc)
            {
                // Completeness is a claim about the WINDOW, so only an in-window exhausted observation
                // degrades it; an exhausted legacy-backlog article is counted (below) but does not relabel
                // this window's coverage.
                exhaustedCompanyIds.Add(exhaustedCompanyId);
            }
        }

        var retryCandidates =
            new List<(NewsTypingInputObservation Observation, DateTimeOffset LastAttemptUtc)>();
        var firstAttempts = new List<NewsTypingInputObservation>();
        foreach (var observation in untyped)
        {
            var untypedKey = (observation.ObservationId, observation.PayloadHash);
            var occupied = occupancy.GetValueOrDefault(untypedKey, AttemptOccupancy.None);
            if (occupied.OccupiedAttempts >= _options.MaxTypingAttempts)
            {
                MarkExhausted(observation);
                continue;
            }

            if (occupied.AttemptedThisRun)
            {
                // Rule (a): within ONE runId an observation that already carries a durable attempt (a
                // reservation or an outcome) is SKIPPED — no model call. Re-running one run costs nothing
                // and advances nothing.
                continue;
            }

            if (occupied.OccupiedAttempts > 0)
            {
                retryCandidates.Add((observation, occupied.LastAttemptUtc));
            }
            else
            {
                firstAttempts.Add(observation);
            }
        }

        // The RETRY LANE: bounded, and FIFO by OLDEST LAST-ATTEMPT INSTANT (then observation id, AD-3), so
        // newly failed work queues BEHIND already-waiting work and every record in a pending snapshot is
        // reached within ceil(pendingRetries / MaxRetryTypingsPerRun) runs. Ordering by attempt count would
        // starve LATER attempts against a continuously replenishing attempt-1 population.
        var retries = retryCandidates
            .OrderBy(c => c.LastAttemptUtc)
            .ThenBy(c => c.Observation.ObservationId)
            .Take(Math.Min(_options.MaxRetryTypingsPerRun, _options.MaxNewTypingsPerRun))
            .Select(c => c.Observation)
            .ToList();

        // The GLOBAL first-attempt order, shared by the two first-attempt lanes so neither re-sorts and
        // they cannot drift. Phase (a): window observations (this run's fresh captures live here), NEWEST
        // first — then phase (b): backlog, OLDEST first. Ties break on observation id (AD-3).
        var windowFirst = firstAttempts
            .Where(o => o.FirstObservedAtUtc > windowStartUtc && o.FirstObservedAtUtc <= asOfUtc)
            .OrderByDescending(o => o.FirstObservedAtUtc)
            .ThenBy(o => o.ObservationId)
            .ToList();
        var backlog = firstAttempts
            .Except(windowFirst)
            .OrderBy(o => o.FirstObservedAtUtc)
            .ThenBy(o => o.ObservationId)
            .ToList();

        // The CANDIDATE lane (spec 187 §2): from the capacity the retry lane left, up to
        // MaxCandidateTypingsPerRun calls spent — ROUND-ROBIN — on the companies this SAME run is about to
        // judge. Clamped to the remaining budget defensively, so a hand-built options instance can never
        // over-select. With no plan the lane is empty and the two lines below reduce EXACTLY to the pre-187
        // §2 expression.
        var firstAttemptCapacity = Math.Max(0, _options.MaxNewTypingsPerRun - retries.Count);
        var candidateSelected = SelectCandidateLane(
            candidatePlan,
            windowFirst,
            backlog,
            Math.Min(_options.MaxCandidateTypingsPerRun, firstAttemptCapacity));

        // The GENERAL lane: every unused slot flows back to the existing global order. An observation the
        // candidate lane already took is INELIGIBLE here — no observation is ever selected twice.
        var candidateKeys = candidateSelected
            .Select(o => (o.ObservationId, o.PayloadHash))
            .ToHashSet();
        var generalSelected = windowFirst
            .Concat(backlog)
            .Where(o => !candidateKeys.Contains((o.ObservationId, o.PayloadHash)))
            .Take(firstAttemptCapacity - candidateSelected.Count)
            .ToList();

        var selected = retries.Concat(candidateSelected).Concat(generalSelected).ToList();

        // ONE lane record, built from the three disjoint lists: the pass-wide counters and the artifact's
        // per-company rows both read it, so the diagnostics cannot disagree with each other.
        var selectionLanes =
            new Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingSelectionLane>();
        foreach (var observation in retries)
        {
            selectionLanes[(observation.ObservationId, observation.PayloadHash)] =
                NewsTypingSelectionLane.Retry;
        }

        foreach (var observation in candidateSelected)
        {
            selectionLanes[(observation.ObservationId, observation.PayloadHash)] =
                NewsTypingSelectionLane.CandidatePriority;
        }

        foreach (var observation in generalSelected)
        {
            selectionLanes[(observation.ObservationId, observation.PayloadHash)] =
                NewsTypingSelectionLane.General;
        }

        var failedCompanyIds = new HashSet<Guid>();
        var newTypings = 0;
        var reservationsRefused = 0;
        var outcomeWritesFailed = 0;

        // Spec 189 §3: the two PER-OBSERVATION records the artifact projects from. `providerCalledKeys` is
        // the one definition of "this pass actually invoked the provider for this observation", so the
        // per-company rows and the pass-wide total can never disagree (a SELECTION is not a CALL: a refused
        // reservation is selected and never called). `retryableFailureKeys` records which observations ended
        // the pass with a failure/refusal/unpersisted outcome, so a company row can name retryable failures
        // separately from ordinary backlog.
        var providerCalledKeys = new HashSet<(Guid ObservationId, string PayloadHash)>();
        var retryableFailureKeys = new HashSet<(Guid ObservationId, string PayloadHash)>();

        // ---- Spec 187 §7 observability. These counters and the timing accumulator influence NOTHING:
        // selection is already fixed above, no branch below reads them, and nothing here is persisted into
        // an id, cohort key or fingerprint. They exist so a live operator can tell a slow provider from a
        // slow collector without waiting for the pass to end.
        var timings = new ProviderCallTimings();
        var attemptedCalls = 0;
        var persistedSuccesses = 0;
        var providerFailures = 0;
        var parseFailures = 0;
        var validationFailures = 0;

        // Spec 185 §5: a failed attempt THIS run degrades the company's typing completeness to Failed
        // (precedence over Backlog). Spec 187 §3 widens "failed attempt" to include a refused reservation
        // and a failed outcome write: a STORAGE failure must never be reported as ordinary backlog.
        void MarkCompanyFailed(NewsTypingInputObservation observation)
        {
            // Spec 189 §3: the observation key is recorded beside the company id, because the artifact
            // reports retryable failures per (company × capture mode) while completeness is per company.
            retryableFailureKeys.Add((observation.ObservationId, observation.PayloadHash));
            if (observation.CompanyId is { } failedCompanyId)
            {
                failedCompanyIds.Add(failedCompanyId);
            }
        }

        void LogTypingProgress() => _logger.LogInformation(
            "News-typing reader {Reader} ({Cohort}) progress: {Attempted}/{Selected} call(s) attempted, "
                + "{Persisted} persisted completed typing(s), failures {ProviderFailures} provider / "
                + "{ParseFailures} parse / {ValidationFailures} validation; stage elapsed {ElapsedMs} ms, "
                + "mean call {MeanMs} ms, max call {MaxMs} ms.",
            reader.Identity.Name,
            cohortKey,
            attemptedCalls,
            selected.Count,
            persistedSuccesses,
            providerFailures,
            parseFailures,
            validationFailures,
            _timeProvider.GetElapsedTime(stageStartTimestamp).TotalMilliseconds.ToString(
                "F0", CultureInfo.InvariantCulture),
            timings.MeanMs.ToString("F1", CultureInfo.InvariantCulture),
            timings.Max.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));

        foreach (var observation in selected)
        {
            ct.ThrowIfCancellationRequested();
            var key = (observation.ObservationId, observation.PayloadHash);

            // ---- Protocol (spec 187 §3). Steps 1 and 2 already removed completed and exhausted work from
            // `selected`; both are re-checked here so the ORDER is enforced at the one site that calls the
            // provider, not merely implied by an earlier filter.
            if (completed.ContainsKey(key))
            {
                continue;
            }

            var occupied = occupancy.GetValueOrDefault(key, AttemptOccupancy.None);
            if (occupied.OccupiedAttempts >= _options.MaxTypingAttempts)
            {
                MarkExhausted(observation);
                continue;
            }

            // ---- Step 3: atomically claim the next ordinal BEFORE any hosted call. The reservation is
            // durable, so a crash between here and the outcome write costs an attempt instead of costing
            // nothing (which is how spec 186's post-hoc count let a call escape the bound).
            var reservation = NewsTypingAttemptReservation.For(
                cohortKey,
                observation.ObservationId,
                observation.PayloadHash,
                attemptOrdinal: occupied.OccupiedAttempts + 1,
                runId,
                reader.Identity.Provider,
                reader.Identity.ModelId,
                _timeProvider.GetUtcNow());
            if (!await _attemptLedger.TryReserveAsync(reservation, ct).ConfigureAwait(false))
            {
                // Another process/invocation won this ordinal, or the ledger write failed. SKIP for this
                // pass — deliberately NOT the following ordinal, which would mint a second concurrent call
                // for the same input and overspend the very budget the ledger exists to bound.
                reservationsRefused++;
                MarkCompanyFailed(observation);
                continue;
            }

            occupied = occupied with { OccupiedAttempts = reservation.AttemptOrdinal };
            occupancy[key] = occupied;

            // ---- Step 4: only the winner calls the provider.
            var record = await TypeOneAsync(reader, cohortKey, runId, observation, reservation, ct)
                .ConfigureAwait(false);

            // Spec 187 §7: a non-null duration IS the record of "a hosted call was made" — the same fact
            // the persisted field carries — so the progress counters and the store can never disagree
            // about how many calls this pass spent.
            if (record.ProviderDurationMs is { } durationMs)
            {
                timings.Record(TimeSpan.FromMilliseconds(durationMs));
                attemptedCalls++;
                providerCalledKeys.Add(key);
            }

            switch (record.Status)
            {
                case NewsTypingStatus.ProviderFailure:
                    providerFailures++;
                    break;
                case NewsTypingStatus.ParseFailure:
                    parseFailures++;
                    break;
                case NewsTypingStatus.ValidationFailed:
                    validationFailures++;
                    break;
                default:
                    break;
            }

            // ---- Steps 5 and 6: persist LINKED to the reservation, and let ONLY a durable outcome count.
            if (await _store.WriteAsync(record, ct).ConfigureAwait(false))
            {
                newTypings++;
                if (record.IsCompletedTyping)
                {
                    completed[key] = record;
                    persistedSuccesses++;
                }
                else
                {
                    MarkCompanyFailed(observation);
                }
            }
            else
            {
                // The attempt is still consumed (the call was made), but nothing durable exists: the record
                // never enters the completed map, never contributes facts/families, and never reaches the
                // stage-2 judge.
                outcomeWritesFailed++;
                orphanedReservations[key] = orphanedReservations.GetValueOrDefault(key) + 1;
                MarkCompanyFailed(observation);
            }

            // ---- Spec 187 §4: the final permitted attempt that left no durably completed typing exhausts
            // the observation NOW, in this run.
            if (!completed.ContainsKey(key)
                && occupied.OccupiedAttempts >= _options.MaxTypingAttempts)
            {
                MarkExhausted(observation);
            }

            if (attemptedCalls > 0 && attemptedCalls % TypingProgressBatchSize == 0)
            {
                LogTypingProgress();
            }
        }

        // The FINAL PARTIAL BATCH: the calls made since the last boundary would otherwise never be
        // reported live. A pass that made no call at all emits no progress line — the summary below states
        // "0 provider call(s)" rather than a progress line implying work happened.
        if (attemptedCalls > 0 && attemptedCalls % TypingProgressBatchSize != 0)
        {
            LogTypingProgress();
        }

        // Spec 187 §4: "remain" means STILL ELIGIBLE — work a later run can actually drain. An exhausted
        // observation is excluded (it is reported by its own counter below), and an attempt whose outcome
        // never persisted still counts as remaining, because nothing durable was produced for it.
        var untypedRemaining = untyped.Count(o =>
            !completed.ContainsKey((o.ObservationId, o.PayloadHash))
                && !exhaustedKeys.Contains((o.ObservationId, o.PayloadHash)));
        _logger.LogInformation(
            "News-typing reader {Reader} ({Cohort}): {New} new typing(s) this pass — lanes: {Retries} "
                + "retry, {CandidatePriority} judgment-candidate priority, {General} general ({Untyped} "
                + "untyped observation(s) remain).",
            reader.Identity.Name,
            cohortKey,
            newTypings,
            retries.Count,
            candidateSelected.Count,
            generalSelected.Count,
            untypedRemaining);

        // Spec 187 §7: the reader's FINAL provider-latency summary — deterministic nearest-rank
        // percentiles over THIS pass's in-memory durations only (never history on disk, so two processes
        // can never disagree about what this pass measured). Contains no model text and no secret: only
        // the reader/cohort identity and numbers.
        _logger.LogInformation(
            "News-typing reader {Reader} ({Cohort}) provider timing: {Timing}.",
            reader.Identity.Name,
            cohortKey,
            timings.Summarize().Describe());

        if (exhaustedKeys.Count > 0)
        {
            // ONE aggregated warning per cohort (the spec-145 precedent): exhaustion removes work from
            // selection permanently, so it must never be silent.
            _logger.LogWarning(
                "News-typing reader {Reader} ({Cohort}): {Exhausted} observation(s) have spent all "
                    + "{MaxAttempts} typing attempt(s) and have LEFT selection; they stay untyped and "
                    + "degrade their company's typing completeness (spec 186 §2).",
                reader.Identity.Name,
                cohortKey,
                exhaustedKeys.Count,
                _options.MaxTypingAttempts);
        }

        if (reservationsRefused > 0 || outcomeWritesFailed > 0)
        {
            // ONE bounded aggregated warning per cohort (spec 187 §3): a storage failure is COUNTED and
            // named, never quietly turned into backlog.
            _logger.LogWarning(
                "News-typing reader {Reader} ({Cohort}): {Refused} attempt reservation(s) could not be "
                    + "claimed and {FailedWrites} outcome write(s) failed this pass. Those observations "
                    + "produced no durable typing, their companies' typing completeness is degraded to "
                    + "Failed for this run, and the stage-2 judge does not see their facts (spec 187 §3).",
                reader.Identity.Name,
                cohortKey,
                reservationsRefused,
                outcomeWritesFailed);
        }

        var reservedWithoutOutcome = orphanedReservations.Values.Sum();
        if (reservedWithoutOutcome > 0)
        {
            _logger.LogWarning(
                "News-typing reader {Reader} ({Cohort}): {Orphans} attempt reservation(s) hold no linked "
                    + "outcome record (crash, cancellation, or a failed outcome write). Each conservatively "
                    + "consumes one of the {MaxAttempts} permitted attempts — the budget can be spent "
                    + "early, but it can never be overspent (spec 187 §3).",
                reader.Identity.Name,
                cohortKey,
                reservedWithoutOutcome,
                _options.MaxTypingAttempts);
        }

        return new ReaderPass(
            reader.Identity,
            completed,
            newTypings,
            failedCompanyIds,
            exhaustedKeys,
            exhaustedCompanyIds,
            orphanedReservations,
            selectionLanes,
            providerCalledKeys,
            retryableFailureKeys,
            new ReaderPassCounters(
                PersistedCompletedOutcomes: persistedSuccesses,
                ProviderFailures: providerFailures,
                ParseFailures: parseFailures,
                ValidationFailures: validationFailures,
                ReservationsRefused: reservationsRefused,
                OutcomeWritesFailed: outcomeWritesFailed,
                UntypedRemaining: untypedRemaining));
    }

    /// <summary>
    /// The spec-187 §2 CANDIDATE first-attempt lane: up to <paramref name="capacity"/> observations drawn
    /// ROUND-ROBIN over the ordered judgment-candidate plan.
    ///
    /// <para>
    /// <b>Why round-robin rather than candidate-at-a-time.</b> The live failure this closes was one noisy
    /// company (EOSE, 31 archived observations) sitting beside 17 other candidates: draining candidate 1's
    /// whole queue before offering candidate 2 anything would reproduce, inside the lane, exactly the
    /// starvation the lane exists to remove. Pass 1 therefore offers every candidate its FIRST eligible
    /// observation in candidate order, pass 2 its second, and so on, so every candidate receives an
    /// observation before any candidate receives a second one.
    /// </para>
    /// <para>
    /// <b>Each candidate's own offer order</b> is its unattempted IN-WINDOW observations newest-first, then
    /// its own legacy BACKLOG oldest-first — the same rule the general lane applies globally, reused by
    /// filtering the already-ordered global lists rather than re-sorting (one ordering definition, and ties
    /// stay on observation id, AD-3).
    /// </para>
    /// <para>
    /// Deterministic and clock-free (AD-3). A <c>null</c>/empty plan or a non-positive capacity selects
    /// NOTHING, which is what makes the judgment-disabled pass byte-identical to spec 186's.
    /// </para>
    /// </summary>
    private static List<NewsTypingInputObservation> SelectCandidateLane(
        NewsJudgmentCandidatePlan? candidatePlan,
        IReadOnlyList<NewsTypingInputObservation> windowFirst,
        IReadOnlyList<NewsTypingInputObservation> backlog,
        int capacity)
    {
        var selected = new List<NewsTypingInputObservation>();
        if (candidatePlan is null || candidatePlan.Count == 0 || capacity <= 0)
        {
            return selected;
        }

        var queues = new List<Queue<NewsTypingInputObservation>>(candidatePlan.Count);
        var seenCompanies = new HashSet<Guid>();
        foreach (var companyId in candidatePlan.CompanyIds)
        {
            // The selector already dedupes by company; the guard keeps a repeated id from earning a second
            // round-robin slot should that ever change.
            if (!seenCompanies.Add(companyId))
            {
                continue;
            }

            var queue = new Queue<NewsTypingInputObservation>(
                windowFirst.Where(o => o.CompanyId == companyId)
                    .Concat(backlog.Where(o => o.CompanyId == companyId)));
            if (queue.Count > 0)
            {
                queues.Add(queue);
            }
        }

        while (selected.Count < capacity)
        {
            var offered = false;
            foreach (var queue in queues)
            {
                if (selected.Count >= capacity)
                {
                    break;
                }

                if (queue.Count == 0)
                {
                    continue;
                }

                selected.Add(queue.Dequeue());
                offered = true;
            }

            if (!offered)
            {
                // Every candidate's queue is drained: the lane gives its remaining capacity back to the
                // general lane rather than holding slots the candidates cannot use.
                break;
            }
        }

        return selected;
    }

    /// <summary>
    /// Builds this cohort's per-(observation, payload) ATTEMPT OCCUPANCY — spec 187 §3's single, total,
    /// deterministic answer to "how many hosted calls has this input already been permitted, when was the
    /// latest one, and did THIS run already attempt it".
    ///
    /// <para>
    /// <b>Occupancy = durable reservations ∪ LEGACY outcomes.</b> A reservation ordinal counts once
    /// (defensively de-duplicated, though the ledger's identity already makes duplicates unrepresentable);
    /// an outcome record counts ONLY when it carries no <c>AttemptReservationId</c>, i.e. it predates spec
    /// 187. That is the whole migration read, and it is what stops accrued attempts being forgotten without
    /// double-counting a modern outcome against the reservation that authorised it.
    /// </para>
    /// <para>
    /// <b>The next ordinal is <c>OccupiedAttempts + 1</c></b>, with legacy outcomes treated as occupying the
    /// LOW ordinals <c>1..legacyCount</c>. Legacy outcomes are frozen (every post-187 outcome is linked), so
    /// allocation is contiguous: <c>legacyCount+1, legacyCount+2, …</c>. Should a non-contiguous ledger ever
    /// arise, the derivation stays total and deterministic and the resulting ordinal is simply already
    /// claimed — <c>TryReserveAsync</c> then returns <c>false</c> and the observation is skipped for the
    /// pass, which is the conservative direction.
    /// </para>
    /// <para>
    /// <b>The FIFO key and the same-run rule are derived HERE, once, from the union.</b> The retry lane
    /// orders by <c>LastAttemptUtc</c> and the same-run idempotency rule reads <c>AttemptedThisRun</c>;
    /// both must see a reservation whose outcome never landed, or a crashed run would re-call on
    /// re-invocation.
    /// </para>
    /// Returns the occupancy map and the per-key count of reservations holding NO linked outcome record.
    /// </summary>
    private static (
        Dictionary<(Guid ObservationId, string PayloadHash), AttemptOccupancy> Occupancy,
        Dictionary<(Guid ObservationId, string PayloadHash), int> OrphanedReservations) BuildOccupancy(
        string cohortKey,
        IReadOnlyList<NewsTypingRecord> cohortRecords,
        IReadOnlyList<NewsTypingAttemptReservation> allReservations,
        Guid? runId)
    {
        var cohortReservations = allReservations
            .Where(r => string.Equals(r.CohortKey, cohortKey, StringComparison.Ordinal))
            .ToList();
        var linkedReservationIds = cohortRecords
            .Where(r => r.AttemptReservationId is not null)
            .Select(r => r.AttemptReservationId!.Value)
            .ToHashSet();

        var occupancy = new Dictionary<(Guid ObservationId, string PayloadHash), AttemptOccupancy>();
        var orphaned = new Dictionary<(Guid ObservationId, string PayloadHash), int>();

        foreach (var group in cohortReservations.GroupBy(r => (r.ObservationId, r.PayloadHash)))
        {
            occupancy[group.Key] = new AttemptOccupancy(
                OccupiedAttempts: group.Select(r => r.AttemptOrdinal).Distinct().Count(),
                LastAttemptUtc: group.Max(r => r.ReservedAtUtc),
                AttemptedThisRun: runId is { } reservedRunId && group.Any(r => r.RunId == reservedRunId));
            var orphans = group.Count(r => !linkedReservationIds.Contains(r.ReservationId));
            if (orphans > 0)
            {
                orphaned[group.Key] = orphans;
            }
        }

        foreach (var group in cohortRecords.GroupBy(r => (r.ObservationId, r.PayloadHash)))
        {
            var current = occupancy.GetValueOrDefault(group.Key, AttemptOccupancy.None);
            var legacyOutcomes = group.Count(r => r.AttemptReservationId is null);
            var lastOutcomeUtc = group.Max(r => r.CreatedAtUtc);
            occupancy[group.Key] = new AttemptOccupancy(
                OccupiedAttempts: current.OccupiedAttempts + legacyOutcomes,
                LastAttemptUtc: lastOutcomeUtc > current.LastAttemptUtc
                    ? lastOutcomeUtc
                    : current.LastAttemptUtc,
                AttemptedThisRun: current.AttemptedThisRun
                    || (runId is { } outcomeRunId && group.Any(r => r.RunId == outcomeRunId)));
        }

        return (occupancy, orphaned);
    }

    /// <summary>
    /// One (cohort, observation, payload)'s attempt occupancy (spec 187 §3): how many hosted calls have
    /// already been PERMITTED (durable reservations plus legacy pre-187 outcomes), when the latest attempt
    /// was recorded (the retry lane's FIFO key), and whether THIS run already attempted it (the same-run
    /// idempotency rule).
    /// </summary>
    private readonly record struct AttemptOccupancy(
        int OccupiedAttempts, DateTimeOffset LastAttemptUtc, bool AttemptedThisRun)
    {
        public static readonly AttemptOccupancy None = new(0, DateTimeOffset.MinValue, false);
    }

    private async Task<NewsTypingRecord> TypeOneAsync(
        NewsTypingReader reader,
        string cohortKey,
        Guid? runId,
        NewsTypingInputObservation observation,
        NewsTypingAttemptReservation reservation,
        CancellationToken ct)
    {
        var baseRecord = BaseRecord(reader.Identity, cohortKey, runId, observation, reservation);

        if (!observation.HasSuppliedText)
        {
            // Defensive only (selection already excludes blank observations): recorded, never modelled.
            return baseRecord with { Status = NewsTypingStatus.NoContent };
        }

        // Spec 187 §7: the provider call is bracketed by the injected TimeProvider's MONOTONIC timestamp
        // APIs. The measurement covers the throwing path too (the elapsed read sits AFTER the catch), so a
        // slow failure — the case most worth seeing — records its duration rather than losing it.
        var callStartTimestamp = _timeProvider.GetTimestamp();
        NewsTypingExtractionOutcome outcome;
        try
        {
            outcome = await reader.Extractor
                .ExtractAsync(new NewsTypingExtractionRequest(observation.Ticker, observation), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Belt-and-braces: the extractor contract types provider failures, but a throwing
            // implementation must still degrade to a recorded provider-failure attempt.
            _logger.LogWarning(
                ex,
                "News-typing reader {Reader} threw for observation {ObservationId}; recording a provider failure.",
                reader.Identity.Name,
                observation.ObservationId);
            outcome = new NewsTypingExtractionOutcome(
                NewsTypingExtractionFailure.ProviderError, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }

        var record = baseRecord with
        {
            RawResponseHash = outcome.RawResponseHash,
            ProviderDurationMs = _timeProvider.GetElapsedTime(callStartTimestamp).TotalMilliseconds,
        };
        switch (outcome.Failure)
        {
            case NewsTypingExtractionFailure.ProviderError:
                return record with
                {
                    Status = NewsTypingStatus.ProviderFailure,
                    FailureDetail = outcome.FailureDetail,
                };
            case NewsTypingExtractionFailure.ParseError:
                return record with
                {
                    Status = NewsTypingStatus.ParseFailure,
                    FailureDetail = outcome.FailureDetail,
                };
            default:
            {
                var validated = NewsTypingClaimValidator.Validate(
                    outcome.Response!, observation, cohortKey);
                return record with
                {
                    Status = validated.Status,
                    Relevance = validated.Relevance,
                    DerivedPrimaryType = validated.DerivedPrimaryType,
                    Facts = validated.Facts,
                    FactsTotal = validated.FactsTotal,
                    FactsAccepted = validated.FactsAccepted,
                    FactsDropped = validated.FactsDropped,
                    FactDropReasons = validated.FactDropReasons,
                };
            }
        }
    }

    private NewsTypingRecord BaseRecord(
        NewsTypingReaderIdentity identity,
        string cohortKey,
        Guid? runId,
        NewsTypingInputObservation observation,
        NewsTypingAttemptReservation reservation) => new(
        SchemaVersion: NewsTypingRecord.CurrentSchemaVersion,
        // Spec 187 §3 keeps spec 186's outcome identity EXACTLY: the run-scoped branch is untouched, and
        // the standalone branch folds the RESERVATION ORDINAL (which equals 186's derived count + 1 for a
        // purely pre-187 history), so every id already on disk is byte-unchanged.
        TypingId: NewsTypingRecord.IdentityFor(
            cohortKey,
            observation.ObservationId,
            observation.PayloadHash,
            runId,
            reservation.AttemptOrdinal),
        RunId: runId,
        ObservationId: observation.ObservationId,
        PayloadHash: observation.PayloadHash,
        CompanyId: observation.CompanyId,
        Ticker: observation.Ticker,
        CaptureMode: observation.CaptureMode,
        ReaderName: identity.Name,
        Provider: identity.Provider,
        ModelId: identity.ModelId,
        PromptVersion: NewsTypingContract.PromptVersion,
        ResultSchemaVersion: NewsTypingContract.SchemaVersion,
        TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        CohortKey: cohortKey,
        Relevance: null,
        DerivedPrimaryType: null,
        Facts: [],
        FactsTotal: 0,
        FactsAccepted: 0,
        FactsDropped: 0,
        FactDropReasons: [],
        Status: NewsTypingStatus.ProviderFailure,
        RawResponseHash: null,
        FailureDetail: null,
        Limits: _options.ToLimitsRecord(),
        ReusedFromTypingId: null,
        CreatedAtUtc: _timeProvider.GetUtcNow(),
        AttemptReservationId: reservation.ReservationId,
        AttemptOrdinal: reservation.AttemptOrdinal);

    private async Task WriteFamilyCheckpointAsync(
        ReaderPass pass,
        DateTimeOffset checkpointUtc,
        DateTimeOffset windowStartUtc,
        DateTimeOffset asOfUtc,
        CancellationToken ct)
    {
        // Spec 186 §4: stage 1 (segmentation, which fixes each episode's DURABLE identity anchor) needs
        // ALL qualifying validated facts — window or not — so an episode's id stops churning when its
        // earliest member ages out of the window. Stage 2 then projects only the episodes with an in-window
        // member. The counters keep their spec-181 WINDOW basis: a checkpoint describes its window.
        var inputs = new List<FactFamilyInputFact>();
        var withoutCompany = 0;
        var considered = 0;
        foreach (var (observation, record) in pass.CompletedWithObservations())
        {
            if (record.Status != NewsTypingStatus.Typed)
            {
                continue;
            }

            var inWindow = observation.FirstObservedAtUtc > windowStartUtc
                && observation.FirstObservedAtUtc <= asOfUtc;
            foreach (var fact in record.Facts)
            {
                if (inWindow)
                {
                    considered++;
                }

                if (observation.CompanyId is not { } companyId)
                {
                    // A family is "the same claim about the same COMPANY" — an unattributed fact cannot
                    // join one, in or out of window. Counted (for the window), never silently dropped.
                    if (inWindow)
                    {
                        withoutCompany++;
                    }

                    continue;
                }

                inputs.Add(new FactFamilyInputFact(
                    FactId: fact.FactId,
                    CompanyId: companyId,
                    EventTypes: fact.EventTypes,
                    Statement: fact.Statement,
                    FirstObservedAtUtc: observation.FirstObservedAtUtc,
                    Publisher: observation.Publisher,
                    ObservationId: observation.ObservationId,
                    CaptureMode: observation.CaptureMode));
            }
        }

        // The projected WINDOW families: what the snapshot records, what the decomposition renders and what
        // the spec-185 judge consumes (every representative resolves in this window's fact index).
        var families = FactFamilyBuilder.Build(inputs, windowStartUtc, asOfUtc);
        pass.Families = families;

        var snapshot = new FactFamilySnapshot(
            SchemaVersion: FactFamilySnapshot.CurrentSchemaVersion,
            BuilderIdentity: FactFamilyBuilder.IdentityString,
            CohortKey: pass.Identity.CohortKey,
            CheckpointUtc: checkpointUtc,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: asOfUtc,
            Families: families,
            FactsConsidered: considered,
            FactsWithoutCompany: withoutCompany);
        await _familyStore
            .WriteAsync(
                NewsTypingCohortPath.PolicySegment(pass.Identity.Provider, pass.Identity.ModelId),
                snapshot,
                ct)
            .ConfigureAwait(false);
    }

    private NewsTypingDecompositionDocument BuildDecomposition(
        Guid? runId,
        IReadOnlyList<ReaderPass> perReader,
        IReadOnlyList<NewsTypingInputObservation> eligible,
        DateTimeOffset windowStartUtc,
        DateTimeOffset asOfUtc,
        bool? captureProven,
        Guid? newsObservationBatchId,
        int? observationsCapturedThisRun,
        DateTimeOffset generatedAtUtc)
    {
        var windowObservations = eligible
            .Where(o => o.FirstObservedAtUtc > windowStartUtc && o.FirstObservedAtUtc <= asOfUtc)
            .ToList();

        var companies = windowObservations
            .Where(o => o.CompanyId is not null)
            .GroupBy(o => o.CompanyId!.Value)
            .OrderBy(g => g.Select(o => o.Ticker).FirstOrDefault(t => t is not null) is null)
            .ThenBy(
                g => g.Select(o => o.Ticker).FirstOrDefault(t => t is not null) ?? string.Empty,
                StringComparer.Ordinal)
            .ThenBy(g => g.Key)
            .Select(g => BuildCompany(g.Key, g.ToList(), perReader, captureProven))
            .ToList();

        return new NewsTypingDecompositionDocument(
            SchemaVersion: NewsTypingDecompositionDocument.CurrentSchemaVersion,
            RunId: runId,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: asOfUtc,
            Caveat: NewsTypingDecompositionDocument.Caveat181,
            Readers: perReader
                .Select(p => $"{p.Identity.Name} ({p.Identity.Provider}:{p.Identity.ModelId})")
                .ToList(),
            CaptureProvenThisRun: captureProven,
            Companies: companies,
            ObservationsWithoutCompany: windowObservations.Count(o => o.CompanyId is null),
            GeneratedAtUtc: generatedAtUtc,
            NewsObservationBatchId: newsObservationBatchId,
            ObservationsCapturedThisRun: observationsCapturedThisRun,
            // Spec 189 §3: the AUTHORITATIVE pass-wide budget view, projected from the SAME per-observation
            // lane/call records the company rows project from — so the two can never disagree about one
            // observation, only about which population they describe (pass-wide versus this window).
            ReaderSummaries: perReader.Select(BuildReaderSummary).ToList());
    }

    /// <summary>
    /// One reader cohort's PASS-WIDE summary (spec 189 §3). Every number is taken from the pass's own
    /// records: the three lane counts and the provider-call count from the per-observation lane/call records,
    /// the outcome counters threaded off the pass loop. Nothing is recomputed from the window, because the
    /// point of this row is precisely that it is NOT a window statement.
    /// </summary>
    private static NewsTypingDecompositionReaderSummary BuildReaderSummary(ReaderPass pass) => new(
        ReaderName: pass.Identity.Name,
        Provider: pass.Identity.Provider,
        ModelId: pass.Identity.ModelId,
        CohortKey: pass.Identity.CohortKey,
        RetrySelected: pass.SelectedIn(NewsTypingSelectionLane.Retry),
        CandidatePrioritySelected: pass.SelectedIn(NewsTypingSelectionLane.CandidatePriority),
        GeneralSelected: pass.SelectedIn(NewsTypingSelectionLane.General),
        ProviderCallsAttempted: pass.ProviderCalledKeys.Count,
        CompletedOutcomesPersisted: pass.Counters.PersistedCompletedOutcomes,
        ProviderFailures: pass.Counters.ProviderFailures,
        ParseFailures: pass.Counters.ParseFailures,
        ValidationFailures: pass.Counters.ValidationFailures,
        ReservationsRefused: pass.Counters.ReservationsRefused,
        OutcomeWritesFailed: pass.Counters.OutcomeWritesFailed,
        RetryExhausted: pass.ExhaustedKeys.Count,
        ReservedWithoutOutcome: pass.OrphanedReservations.Values.Sum(),
        UntypedRemaining: pass.Counters.UntypedRemaining);

    private static NewsTypingDecompositionCompany BuildCompany(
        Guid companyId,
        IReadOnlyList<NewsTypingInputObservation> companyObservations,
        IReadOnlyList<ReaderPass> perReader,
        bool? captureProven)
    {
        var cohorts = new List<NewsTypingDecompositionCohort>();
        var incompleteReasons = new List<string>();
        if (captureProven != true)
        {
            incompleteReasons.Add(
                "capture not proven for this run (no batch manifest, or the manifest records "
                    + "failures/partial universe)");
        }

        foreach (var pass in perReader)
        {
            foreach (var modeGroup in companyObservations
                .GroupBy(o => o.CaptureMode)
                .OrderBy(g => (int)g.Key))
            {
                var typedRecords = new List<(NewsTypingInputObservation Observation, NewsTypingRecord Record)>();
                var insufficient = 0;
                var untyped = 0;
                var exhausted = 0;
                var reservedWithoutOutcome = 0;
                var candidatePrioritySelected = 0;
                var generalSelected = 0;
                var retrySelected = 0;
                var providerCallsAttempted = 0;
                var retryableFailures = 0;
                foreach (var observation in modeGroup)
                {
                    var key = (observation.ObservationId, observation.PayloadHash);
                    reservedWithoutOutcome += pass.OrphanedReservations.GetValueOrDefault(key);

                    // Spec 189 §3: what this pass SPENT on this observation, and whether it ended the pass
                    // degraded-but-still-eligible. Both are projected from the pass's per-observation
                    // records, so the pass-wide reader summary and this row cannot disagree.
                    if (pass.ProviderCalledKeys.Contains(key))
                    {
                        providerCallsAttempted++;
                    }

                    if (pass.IsRetryableFailure(key))
                    {
                        retryableFailures++;
                    }

                    // Spec 187 §2, completed by spec 189 §3: this company's IN-WINDOW share of ALL THREE
                    // lanes (the rows are a window statement — the pass-wide totals live on the reader
                    // summary and the cohort run result).
                    switch (pass.SelectionLanes.GetValueOrDefault(key, NewsTypingSelectionLane.NotSelected))
                    {
                        case NewsTypingSelectionLane.Retry:
                            retrySelected++;
                            break;
                        case NewsTypingSelectionLane.CandidatePriority:
                            candidatePrioritySelected++;
                            break;
                        case NewsTypingSelectionLane.General:
                            generalSelected++;
                            break;
                        default:
                            break;
                    }

                    if (!pass.Completed.TryGetValue(key, out var record))
                    {
                        // Spec 187 §4: the two sets are DISJOINT. Before 187 an exhausted observation was
                        // counted as backlog AND as exhausted, so the row over-stated recoverable work and
                        // the company rendered BOTH incomplete reasons for one observation.
                        // `UntypedRemaining` now means "still eligible for a future first attempt or
                        // retry" — work a later run can actually drain.
                        if (pass.ExhaustedKeys.Contains(key))
                        {
                            exhausted++;
                        }
                        else
                        {
                            untyped++;
                        }
                    }
                    else if (record.Status == NewsTypingStatus.InsufficientContent)
                    {
                        insufficient++;
                    }
                    else
                    {
                        typedRecords.Add((observation, record));
                    }
                }

                var cohortFamilies = pass.Families
                    .Where(f => f.CompanyId == companyId && f.CaptureMode == modeGroup.Key)
                    .ToList();

                var typeRows = typedRecords
                    .Where(t => t.Record.DerivedPrimaryType is not null)
                    .GroupBy(t => t.Record.DerivedPrimaryType!.Value)
                    .Select(g =>
                    {
                        // The row counts observations by DerivedPrimaryType, so its family count must
                        // share that basis: only families containing one of THESE observations' facts
                        // of this type — never any cohort family that merely mentions the type.
                        var rowFactIds = g
                            .SelectMany(t => t.Record.Facts)
                            .Where(f => f.EventTypes.Contains(g.Key))
                            .Select(f => f.FactId)
                            .ToHashSet();
                        return new NewsTypingDecompositionTypeRow(
                            EventType: g.Key,
                            ObservationCount: g.Count(),
                            PublisherBreadth: g
                                .Select(t => t.Observation.Publisher)
                                .Distinct(StringComparer.Ordinal)
                                .Count(),
                            FamilyCount: cohortFamilies
                                .Count(f => f.MemberFactIds.Any(rowFactIds.Contains)));
                    })
                    .OrderByDescending(r => r.ObservationCount)
                    .ThenBy(r => (int)r.EventType)
                    .ToList();

                if (untyped > 0)
                {
                    incompleteReasons.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"typing backlog: {untyped} observation(s) untyped for {pass.Identity.Name} "
                            + $"({modeGroup.Key})"));
                }

                if (exhausted > 0)
                {
                    // Spec 186 §2: named separately from the backlog — a later run will NOT drain these.
                    incompleteReasons.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"typing retries exhausted: {exhausted} observation(s) will not be typed for "
                            + $"{pass.Identity.Name} ({modeGroup.Key})"));
                }

                if (retryableFailures > 0)
                {
                    // Spec 189 §3: named separately from BOTH — the observation failed today and is still
                    // eligible, which is neither "deferred by the cap" nor "will never be typed". The
                    // sentence says so explicitly rather than leaving the reader to infer the remedy.
                    incompleteReasons.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"typing retryable failure this run: {retryableFailures} observation(s) for "
                            + $"{pass.Identity.Name} ({modeGroup.Key}); they remain in the eligible backlog"));
                }

                cohorts.Add(new NewsTypingDecompositionCohort(
                    ReaderName: pass.Identity.Name,
                    Provider: pass.Identity.Provider,
                    ModelId: pass.Identity.ModelId,
                    CohortKey: pass.Identity.CohortKey,
                    CaptureMode: modeGroup.Key,
                    ObservationsTyped: typedRecords.Count,
                    ObservationsInsufficientContent: insufficient,
                    UntypedRemaining: untyped,
                    FamilyCount: cohortFamilies.Count,
                    Types: typeRows,
                    RetryExhausted: exhausted,
                    ReservedWithoutOutcome: reservedWithoutOutcome,
                    CandidatePrioritySelected: candidatePrioritySelected,
                    GeneralSelected: generalSelected,
                    RetrySelected: retrySelected,
                    ProviderCallsAttempted: providerCallsAttempted,
                    RetryableFailuresThisRun: retryableFailures));
            }
        }

        return new NewsTypingDecompositionCompany(
            CompanyId: companyId,
            Ticker: companyObservations.Select(o => o.Ticker).FirstOrDefault(t => t is not null),
            ObservationsInWindow: companyObservations.Count,
            Incomplete: incompleteReasons.Count > 0,
            IncompleteReasons: incompleteReasons,
            Cohorts: cohorts);
    }

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

    /// <summary>
    /// Which of the three spec-187 §2 lanes selected an observation for a hosted call this pass. The lanes
    /// are DISJOINT by construction (an observation selected by an earlier lane is ineligible for the later
    /// ones), so this is a total classification of everything the pass selected — and
    /// <see cref="NotSelected"/> is the zero value, so an observation the pass never touched can never be
    /// mistaken for one it chose.
    /// </summary>
    private enum NewsTypingSelectionLane
    {
        /// <summary>Not selected this pass (deferred by a cap, already completed, or exhausted).</summary>
        NotSelected = 0,

        /// <summary>The bounded spec-186 §2 retry lane — globally FIFO, deliberately NOT candidate-aware.</summary>
        Retry,

        /// <summary>The bounded round-robin lane over the companies this run is about to judge (spec 187 §2).</summary>
        CandidatePriority,

        /// <summary>The global first-attempt queue: window newest-first, then backlog oldest-first.</summary>
        General,
    }

    /// <summary>
    /// One reader pass's PASS-WIDE outcome counters (spec 189 §3). They are the durable artifact equivalent
    /// of the bounded/final log totals this pass already emitted, threaded onto the pass rather than
    /// recomputed, so the reader summary and the log lines cannot disagree. Diagnostics only: nothing here
    /// is hashed, and no selection, validation or family decision reads them.
    /// </summary>
    private readonly record struct ReaderPassCounters(
        int PersistedCompletedOutcomes,
        int ProviderFailures,
        int ParseFailures,
        int ValidationFailures,
        int ReservationsRefused,
        int OutcomeWritesFailed,
        int UntypedRemaining);

    /// <summary>
    /// One reader's per-pass state: its completed-cache view, its new-typing count, its checkpoint families,
    /// the spec-186 §2 retry-exhaustion view (which observations left selection, and whose companies), the
    /// spec-187 §2 per-observation selection-lane record, and spec 189 §3's per-observation record of which
    /// observations this pass actually CALLED the provider for and which ended it with a retryable failure.
    /// </summary>
    private sealed class ReaderPass(
        NewsTypingReaderIdentity identity,
        Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingRecord> completed,
        int newTypings,
        HashSet<Guid> failedCompanyIds,
        HashSet<(Guid ObservationId, string PayloadHash)> exhaustedKeys,
        HashSet<Guid> exhaustedCompanyIds,
        Dictionary<(Guid ObservationId, string PayloadHash), int> orphanedReservations,
        Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingSelectionLane> selectionLanes,
        HashSet<(Guid ObservationId, string PayloadHash)> providerCalledKeys,
        HashSet<(Guid ObservationId, string PayloadHash)> retryableFailureKeys,
        ReaderPassCounters counters)
    {
        public NewsTypingReaderIdentity Identity { get; } = identity;

        public Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingRecord> Completed { get; } =
            completed;

        public int NewTypings { get; } = newTypings;

        /// <summary>Companies with at least one FAILED typing attempt this run (spec 185 §5 completeness precedence).</summary>
        public HashSet<Guid> FailedCompanyIds { get; } = failedCompanyIds;

        /// <summary>Untyped observations whose typing attempts are EXHAUSTED — they left selection (spec 186 §2).</summary>
        public HashSet<(Guid ObservationId, string PayloadHash)> ExhaustedKeys { get; } = exhaustedKeys;

        /// <summary>Companies holding at least one exhausted untyped observation — never <c>Complete</c> (spec 186 §2).</summary>
        public HashSet<Guid> ExhaustedCompanyIds { get; } = exhaustedCompanyIds;

        /// <summary>
        /// Per (observation, payload): how many durable attempt RESERVATIONS hold no linked outcome record
        /// (spec 187 §3) — a hosted call spent with nothing persisted. A diagnostic, never a partition
        /// member: the observation is already counted as untyped-eligible or exhausted.
        /// </summary>
        public Dictionary<(Guid ObservationId, string PayloadHash), int> OrphanedReservations { get; } =
            orphanedReservations;

        /// <summary>
        /// Which lane selected each observation this pass (spec 187 §2). The ONE record behind both the
        /// per-cohort pass-wide counters and the artifact's per-company in-window counts, so the two
        /// diagnostics can never disagree. Selection order only — it touches no typing content, no
        /// validation, no cohort identity and no family membership.
        /// </summary>
        public Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingSelectionLane>
            SelectionLanes { get; } = selectionLanes;

        /// <summary>How many observations this pass selected in the given lane (pass-wide, window and backlog alike).</summary>
        public int SelectedIn(NewsTypingSelectionLane lane) =>
            SelectionLanes.Values.Count(l => l == lane);

        /// <summary>
        /// Spec 189 §3: the observations this pass actually invoked the provider for — the SAME fact
        /// <c>NewsTypingRecord.ProviderDurationMs</c> carries, recorded at the one site that measures a call.
        /// A SELECTION is not a CALL: a refused reservation, a completed-cache hit re-checked at the call
        /// site and an exhausted observation are all selected-but-never-called, so the two counts are
        /// deliberately different numbers and must never be equated.
        /// </summary>
        public HashSet<(Guid ObservationId, string PayloadHash)> ProviderCalledKeys { get; } =
            providerCalledKeys;

        /// <summary>
        /// Spec 189 §3: the observations that ended this pass with a provider/parse/validation failure, a
        /// refused attempt reservation or an outcome write that never persisted. Use
        /// <see cref="IsRetryableFailure"/> rather than this set directly — an observation that failed AND
        /// exhausted its budget on the same attempt is EXHAUSTED, not retryable.
        /// </summary>
        public HashSet<(Guid ObservationId, string PayloadHash)> RetryableFailureKeys { get; } =
            retryableFailureKeys;

        /// <summary>This pass's PASS-WIDE outcome counters (spec 189 §3) — the artifact's authoritative budget view.</summary>
        public ReaderPassCounters Counters { get; } = counters;

        /// <summary>
        /// Whether this observation ended the pass with a RETRYABLE failure: it failed, and it has NOT spent
        /// its attempt budget. An exhausted observation is reported by <see cref="ExhaustedKeys"/> alone, so
        /// the two diagnostics never double-count one observation.
        /// </summary>
        public bool IsRetryableFailure((Guid ObservationId, string PayloadHash) key) =>
            RetryableFailureKeys.Contains(key) && !ExhaustedKeys.Contains(key);

        public IReadOnlyList<FactFamilyRecord> Families { get; set; } = [];

        public Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingInputObservation>?
            ObservationIndex { get; set; }

        public IEnumerable<(NewsTypingInputObservation Observation, NewsTypingRecord Record)>
            CompletedWithObservations()
        {
            foreach (var (key, record) in Completed.OrderBy(kv => kv.Value.TypingId))
            {
                if (ObservationIndex is not null && ObservationIndex.TryGetValue(key, out var observation))
                {
                    yield return (observation, record);
                }
            }
        }
    }
}
