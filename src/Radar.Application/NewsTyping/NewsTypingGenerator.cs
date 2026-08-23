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
    /// </summary>
    Task GenerateAsync(Guid? runId, CancellationToken ct);
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

    public async Task GenerateAsync(Guid? runId, CancellationToken ct)
    {
        var fallbackDateToken = _timeProvider.GetUtcNow().UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            await GenerateCoreAsync(runId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A typing failure must never abort or relabel the already-durable Radar run: write the named
            // failed artifact (itself best-effort) and return.
            _logger.LogError(ex, "News-typing pass failed; writing the named failed artifact.");
            await _artifactStore
                .WriteFailedAsync(fallbackDateToken, $"{ex.GetType().Name}: {ex.Message}", ct)
                .ConfigureAwait(false);
        }
    }

    private async Task GenerateCoreAsync(Guid? runId, CancellationToken ct)
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

        // Checkpoint fact families per cohort (spec 181 §4): over ALL validated facts from COMPLETED
        // typings in the window — never only this run's new facts (which would miss duplicates typed by
        // earlier runs).
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

        // Completed cache for this cohort keyed by (observation, payload) — deterministic winner: most
        // recent CreatedAtUtc, then lowest TypingId (the FindCompletedAsync rule, AD-3).
        var completed = allRecords
            .Where(r => string.Equals(r.CohortKey, cohortKey, StringComparison.Ordinal)
                && r.IsCompletedTyping)
            .GroupBy(r => (r.ObservationId, r.PayloadHash))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.TypingId).First());

        var untyped = eligible
            .Where(o => !completed.ContainsKey((o.ObservationId, o.PayloadHash)))
            .ToList();

        // Phase (a): window observations (this run's fresh captures live here), NEWEST first — then
        // phase (b): backlog, OLDEST first. Ties break on observation id (AD-3). One overall per-reader cap.
        var windowFirst = untyped
            .Where(o => o.FirstObservedAtUtc > windowStartUtc && o.FirstObservedAtUtc <= asOfUtc)
            .OrderByDescending(o => o.FirstObservedAtUtc)
            .ThenBy(o => o.ObservationId)
            .ToList();
        var backlog = untyped
            .Except(windowFirst)
            .OrderBy(o => o.FirstObservedAtUtc)
            .ThenBy(o => o.ObservationId)
            .ToList();
        var selected = windowFirst.Concat(backlog).Take(_options.MaxNewTypingsPerRun).ToList();

        var newTypings = 0;
        foreach (var observation in selected)
        {
            ct.ThrowIfCancellationRequested();
            var record = await TypeOneAsync(reader, cohortKey, runId, observation, ct)
                .ConfigureAwait(false);
            await _store.WriteAsync(record, ct).ConfigureAwait(false);
            newTypings++;
            if (record.IsCompletedTyping)
            {
                completed[(observation.ObservationId, observation.PayloadHash)] = record;
            }
        }

        _logger.LogInformation(
            "News-typing reader {Reader} ({Cohort}): {New} new typing(s) this pass "
                + "({Untyped} untyped observation(s) remain).",
            reader.Identity.Name,
            cohortKey,
            newTypings,
            untyped.Count - newTypings);

        return new ReaderPass(reader.Identity, completed, newTypings);
    }

    private async Task<NewsTypingRecord> TypeOneAsync(
        NewsTypingReader reader,
        string cohortKey,
        Guid? runId,
        NewsTypingInputObservation observation,
        CancellationToken ct)
    {
        var baseRecord = BaseRecord(reader.Identity, cohortKey, runId, observation);

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
        NewsTypingInputObservation observation) => new(
        SchemaVersion: NewsTypingRecord.CurrentSchemaVersion,
        TypingId: NewsTypingRecord.IdentityFor(
            cohortKey, observation.ObservationId, observation.PayloadHash, runId),
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
        var inputs = new List<FactFamilyInputFact>();
        var withoutCompany = 0;
        var considered = 0;
        foreach (var (observation, record) in pass.CompletedWithObservations())
        {
            if (observation.FirstObservedAtUtc <= windowStartUtc
                || observation.FirstObservedAtUtc > asOfUtc
                || record.Status != NewsTypingStatus.Typed)
            {
                continue;
            }

            foreach (var fact in record.Facts)
            {
                considered++;
                if (observation.CompanyId is not { } companyId)
                {
                    // A family is "the same claim about the same COMPANY" — an unattributed fact cannot
                    // join one. Counted, never silently dropped.
                    withoutCompany++;
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

        var families = FactFamilyBuilder.Build(inputs);
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
                foreach (var observation in modeGroup)
                {
                    if (!pass.Completed.TryGetValue(
                        (observation.ObservationId, observation.PayloadHash), out var record))
                    {
                        untyped++;
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
                    Types: typeRows));
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

    /// <summary>One reader's per-pass state: its completed-cache view, its new-typing count and its checkpoint families.</summary>
    private sealed class ReaderPass(
        NewsTypingReaderIdentity identity,
        Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingRecord> completed,
        int newTypings)
    {
        public NewsTypingReaderIdentity Identity { get; } = identity;

        public Dictionary<(Guid ObservationId, string PayloadHash), NewsTypingRecord> Completed { get; } =
            completed;

        public int NewTypings { get; } = newTypings;

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
