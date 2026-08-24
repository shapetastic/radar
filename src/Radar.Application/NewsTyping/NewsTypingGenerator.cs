using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.News;
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
    /// </summary>
    Task<NewsTypingRunResult?> GenerateAsync(Guid? runId, CancellationToken ct);
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
/// <item><b>Retries are BOUNDED and FAIR (spec 186 §2).</b> Attempt counts are DERIVED per (cohort,
/// observation, payload) from the insert-only store's own records; an observation that has spent
/// <see cref="NewsTypingOptions.MaxTypingAttempts"/> hosted calls LEAVES selection (counted, warned once per
/// cohort, and its company's typing completeness degrades). Retries occupy their own bounded FIFO lane
/// (<see cref="NewsTypingOptions.MaxRetryTypingsPerRun"/>, oldest last-attempt first) inside the per-run cap,
/// so neither the backlog nor the retry queue can starve the other.</item>
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

    private readonly IPipelineRunStore _runStore;
    private readonly INewsObservationArchive _observationArchive;
    private readonly INewsObservationBatchReader _batchReader;
    private readonly NewsTypingReaderSet _readers;
    private readonly INewsTypingStore _store;
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
        _familyStore = familyStore;
        _artifactStore = artifactStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<NewsTypingRunResult?> GenerateAsync(Guid? runId, CancellationToken ct)
    {
        var fallbackDateToken = _timeProvider.GetUtcNow().UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            return await GenerateCoreAsync(runId, ct).ConfigureAwait(false);
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

    private async Task<NewsTypingRunResult> GenerateCoreAsync(Guid? runId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var runRecord = runId is { } id ? await FindRunRecordAsync(id, ct).ConfigureAwait(false) : null;
        var asOfUtc = runRecord?.CreatedAtUtc ?? now;
        var asOfDateToken = asOfUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var windowStartUtc = asOfUtc.AddDays(-_options.LookbackDays);

        // Capture provenance for THIS run, resolved fail-closed: no resolvable batch manifest means
        // UNKNOWN, which the artifact renders as unproven — never as a clean batch.
        bool? captureProven = null;
        if (runRecord?.NewsObservationBatchId is { } batchId)
        {
            var batch = await _batchReader.GetBatchAsync(batchId, ct).ConfigureAwait(false);
            captureProven = batch is null ? null : batch.CaptureProven && batch.FullUniverse;
        }

        var observations = await _observationArchive.GetAllAsync(ct).ConfigureAwait(false);
        var eligible = observations
            .Select(NewsTypingInputObservation.FromRecord)
            .Where(o => o.HasSuppliedText)
            .ToList();

        // One durable read, indexed in memory: the completed-typing cache lookup for EVERY reader without
        // an O(observations × records) scan per reader.
        var allRecords = await _store.GetAllAsync(ct).ConfigureAwait(false);

        var observationIndex = eligible.ToDictionary(o => (o.ObservationId, o.PayloadHash));
        var perReader = new List<ReaderPass>(_readers.Readers.Count);
        foreach (var reader in _readers.Readers)
        {
            ct.ThrowIfCancellationRequested();
            var pass = await RunReaderPassAsync(
                reader, runId, eligible, allRecords, windowStartUtc, asOfUtc, ct).ConfigureAwait(false);
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
            runId, perReader, eligible, windowStartUtc, asOfUtc, captureProven, now);
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
    /// typings (the same set the family checkpoint consumed); the per-company completeness map follows the
    /// spec's precedence — a failed attempt this run outranks a backlog, which outranks complete — and a
    /// company with zero in-window observations is vacuously <see cref="NewsTypingCompleteness.Complete"/>
    /// (it also has zero facts, so the judge records <c>InsufficientFacts</c> for it anyway).
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

                // Spec 186 §2: an exhausted untyped observation is a PERMANENT hole, not a deferral, so the
                // company can never read Complete — and "Backlog" (deferred by the per-run cap) would be a
                // false statement about it. It maps onto the existing degraded state, Failed.
                completeness[companyGroup.Key] = pass.FailedCompanyIds.Contains(companyGroup.Key)
                    || pass.ExhaustedCompanyIds.Contains(companyGroup.Key)
                    ? NewsTypingCompleteness.Failed
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
            RetryExhausted: pass.ExhaustedKeys.Count);
    }

    private async Task<ReaderPass> RunReaderPassAsync(
        NewsTypingReader reader,
        Guid? runId,
        IReadOnlyList<NewsTypingInputObservation> eligible,
        IReadOnlyList<NewsTypingRecord> allRecords,
        DateTimeOffset windowStartUtc,
        DateTimeOffset asOfUtc,
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

        // Spec 186 §2: attempt history DERIVED per (cohort, observation, payload) from the records the
        // insert-only store already holds — no new store, no side index. This is what bounds HOSTED CALLS.
        var attempts = cohortRecords
            .GroupBy(r => (r.ObservationId, r.PayloadHash))
            .ToDictionary(
                g => g.Key,
                g => new AttemptHistory(
                    Count: g.Count(),
                    LastAttemptUtc: g.Max(r => r.CreatedAtUtc),
                    AttemptedThisRun: runId is { } thisRunId && g.Any(r => r.RunId == thisRunId)));

        var untyped = eligible
            .Where(o => !completed.ContainsKey((o.ObservationId, o.PayloadHash)))
            .ToList();

        // Split the untyped set into the spec-186 §2 tiers. Order matters: EXHAUSTION is a durable property
        // of the record set and is reported whether or not this run already touched the observation, while
        // the same-run skip is an idempotency rule about THIS invocation.
        var exhaustedKeys = new HashSet<(Guid ObservationId, string PayloadHash)>();
        var exhaustedCompanyIds = new HashSet<Guid>();
        var retryCandidates =
            new List<(NewsTypingInputObservation Observation, DateTimeOffset LastAttemptUtc)>();
        var firstAttempts = new List<NewsTypingInputObservation>();
        foreach (var observation in untyped)
        {
            var untypedKey = (observation.ObservationId, observation.PayloadHash);
            var history = attempts.GetValueOrDefault(untypedKey, AttemptHistory.None);
            if (history.Count >= _options.MaxTypingAttempts)
            {
                exhaustedKeys.Add(untypedKey);
                if (observation.CompanyId is { } exhaustedCompanyId
                    && observation.FirstObservedAtUtc > windowStartUtc
                    && observation.FirstObservedAtUtc <= asOfUtc)
                {
                    // Completeness is a claim about the WINDOW, so only an in-window exhausted observation
                    // degrades it; an exhausted legacy-backlog article is counted (below) but does not
                    // relabel this window's coverage.
                    exhaustedCompanyIds.Add(exhaustedCompanyId);
                }

                continue;
            }

            if (history.AttemptedThisRun)
            {
                // Rule (a): within ONE runId an observation that already carries a persisted attempt is
                // SKIPPED — no model call. Re-running one run costs nothing and advances nothing.
                continue;
            }

            if (history.Count > 0)
            {
                retryCandidates.Add((observation, history.LastAttemptUtc));
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

        // UNUSED lane capacity returns to first attempts. Phase (a): window observations (this run's fresh
        // captures live here), NEWEST first — then phase (b): backlog, OLDEST first. Ties break on
        // observation id (AD-3). One overall per-reader cap across both lanes.
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
        var selected = retries
            .Concat(windowFirst.Concat(backlog).Take(_options.MaxNewTypingsPerRun - retries.Count))
            .ToList();

        var failedCompanyIds = new HashSet<Guid>();
        var newTypings = 0;
        foreach (var observation in selected)
        {
            ct.ThrowIfCancellationRequested();
            var key = (observation.ObservationId, observation.PayloadHash);
            var attemptNumber = attempts.GetValueOrDefault(key, AttemptHistory.None).Count + 1;
            var record = await TypeOneAsync(reader, cohortKey, runId, observation, attemptNumber, ct)
                .ConfigureAwait(false);
            await _store.WriteAsync(record, ct).ConfigureAwait(false);
            newTypings++;
            if (record.IsCompletedTyping)
            {
                completed[key] = record;
            }
            else if (observation.CompanyId is { } failedCompanyId)
            {
                // Spec 185 §5: a failed attempt THIS run degrades the company's typing completeness to
                // Failed (precedence over Backlog) in this cohort's run result.
                failedCompanyIds.Add(failedCompanyId);
            }
        }

        _logger.LogInformation(
            "News-typing reader {Reader} ({Cohort}): {New} new typing(s) this pass ({Retries} retry, "
                + "{Untyped} untyped observation(s) remain).",
            reader.Identity.Name,
            cohortKey,
            newTypings,
            retries.Count,
            untyped.Count - newTypings);

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

        return new ReaderPass(
            reader.Identity, completed, newTypings, failedCompanyIds, exhaustedKeys, exhaustedCompanyIds);
    }

    /// <summary>
    /// One (cohort, observation, payload)'s DERIVED attempt history (spec 186 §2): how many attempts the
    /// insert-only store already holds, when the latest one was recorded (the retry lane's FIFO key), and
    /// whether THIS run already attempted it (the same-run idempotency rule).
    /// </summary>
    private readonly record struct AttemptHistory(
        int Count, DateTimeOffset LastAttemptUtc, bool AttemptedThisRun)
    {
        public static readonly AttemptHistory None = new(0, DateTimeOffset.MinValue, false);
    }

    private async Task<NewsTypingRecord> TypeOneAsync(
        NewsTypingReader reader,
        string cohortKey,
        Guid? runId,
        NewsTypingInputObservation observation,
        int attemptNumber,
        CancellationToken ct)
    {
        var baseRecord = BaseRecord(reader.Identity, cohortKey, runId, observation, attemptNumber);

        if (!observation.HasSuppliedText)
        {
            // Defensive only (selection already excludes blank observations): recorded, never modelled.
            return baseRecord with { Status = NewsTypingStatus.NoContent };
        }

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

        var record = baseRecord with { RawResponseHash = outcome.RawResponseHash };
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
        int attemptNumber) => new(
        SchemaVersion: NewsTypingRecord.CurrentSchemaVersion,
        TypingId: NewsTypingRecord.IdentityFor(
            cohortKey, observation.ObservationId, observation.PayloadHash, runId, attemptNumber),
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
        CreatedAtUtc: _timeProvider.GetUtcNow());

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
            GeneratedAtUtc: generatedAtUtc);
    }

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
                foreach (var observation in modeGroup)
                {
                    if (!pass.Completed.TryGetValue(
                        (observation.ObservationId, observation.PayloadHash), out var record))
                    {
                        untyped++;
                        if (pass.ExhaustedKeys.Contains(
                            (observation.ObservationId, observation.PayloadHash)))
                        {
                            exhausted++;
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
                    RetryExhausted: exhausted));
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
    /// One reader's per-pass state: its completed-cache view, its new-typing count, its checkpoint families
    /// and the spec-186 §2 retry-exhaustion view (which observations left selection, and whose companies).
    /// </summary>
    private sealed class ReaderPass(
        NewsTypingReaderIdentity identity,
        Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingRecord> completed,
        int newTypings,
        HashSet<Guid> failedCompanyIds,
        HashSet<(Guid ObservationId, string PayloadHash)> exhaustedKeys,
        HashSet<Guid> exhaustedCompanyIds)
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
