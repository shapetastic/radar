using System.Globalization;

using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Filings;
using Radar.Application.News;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Pipeline;

/// <summary>
/// Stages 1–5, extracted verbatim from <see cref="RadarPipelineRunner"/> (spec 144) so the same code runs in
/// the combined pass and in a standalone <c>collect</c> pass. The collect stage runs <b>all</b> registered
/// collectors in a stable <see cref="IEvidenceCollector.CollectorName"/> order and merges their results (via
/// <see cref="CollectionResultMerger"/>) before storing evidence. Contains <b>no</b> scoring math and
/// <b>no</b> resolution/extraction logic — each stage's behaviour stays behind its own interface; this only
/// sequences them.
/// <para>
/// Spec 137's "collection is singular" rule is enforced HERE, by construction: collection, the AI directional
/// read, extraction, resolution, review and signal persistence all live in this one pass, which every caller
/// invokes exactly once per run. Nothing in this type is strategy-aware.
/// </para>
/// </summary>
public sealed class CollectionPass : ICollectionPass
{
    private readonly IReadOnlyList<IEvidenceCollector> _collectors;
    private readonly CollectedEvidenceMapper _mapper;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IRawEvidenceStore _rawEvidenceStore;
    private readonly ISignalExtractor _extractor;
    private readonly ICompanyResolver _resolver;
    private readonly ISignalReviewer _reviewer;
    private readonly ISignalRepository _signalRepository;
    private readonly ISignalReviewRepository _signalReviewRepository;
    private readonly ISignalFileStore _signalFileStore;
    private readonly ICompanyRepository _companyRepository;
    private readonly ICollectionHealthValidator _healthValidator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CollectionPass> _logger;

    // The curated attention publisher tier map (spec 196 §3). REQUIRED, never optional-nullable: a silently
    // null optional dependency means a production wiring mistake renders no coverage diagnostic while every
    // test stays green (spec 150's precedent). It is registered unconditionally by
    // AddRadarApplicationServices, so every composition that can build a CollectionPass can supply it.
    // Read-only and observational here — the pass never scores anything.
    private readonly IAttentionSourceWeights _attentionSourceWeights;

    // OPT-IN directional filing enrichment (AI only). Null when AI is disabled (the shipped default), in
    // which case the enrichment step is skipped entirely and the default pipeline is byte-for-byte
    // unchanged. .NET DI supplies the null default when the service is not registered.
    private readonly IDirectionalFilingSignalSource? _directionalFilingSignals;

    // OPT-IN point-in-time news observation archive (spec 177). Null when capture is disabled or this is a
    // composition that never registered it, in which case the capture step is skipped entirely and the
    // pass is byte-for-byte unchanged. Observational only: capture never aborts the run, never touches a
    // counter above, and nothing in the evidence → signal → score path reads the archive.
    private readonly INewsObservationArchive? _newsObservationArchive;
    private readonly NewsObservationCaptureOptions _newsObservationCaptureOptions;

    // OPT-IN reported-metrics ledger (spec 215 §1). Null when the AI path is disabled or the ledger is not
    // registered, in which case a fresh filing read's extraction is COUNTED as unwritten in the one
    // aggregated line below and the pass is otherwise byte-for-byte unchanged. Written HERE, not in the
    // directional source, because the ledger record needs the RESOLVED company id and company resolution
    // lives in exactly one place (MapResolveReviewStoreAsync).
    private readonly IReportedMetricStore? _reportedMetricStore;

    // OPT-IN reported-metrics OUTBOX (spec 216 §2), registered with the ledger and never separately. It is
    // the durable payload that makes a failed ledger write RETRYABLE instead of merely counted, and it is
    // what the analyzed-filing cache stamp now MEANS ("an envelope exists for this accession under this
    // policy"). Null when the ledger is not registered.
    private readonly IReportedMetricOutbox? _reportedMetricOutbox;

    // OPT-IN analyzed-filing cache (spec 216 §2). The pass needs it because the reported-metrics policy
    // stamp MOVED here: the directional source now writes its cache record unstamped, and this pass stamps
    // it only once the outbox envelope is durable. Null in a composition that registers no cache, in which
    // case the stamp is counted as unwritten and the filing simply re-analyzes next run.
    private readonly IAnalyzedFilingCache? _analyzedFilingCache;

    public CollectionPass(
        IEnumerable<IEvidenceCollector> collectors,
        CollectedEvidenceMapper mapper,
        IEvidenceRepository evidenceRepository,
        IRawEvidenceStore rawEvidenceStore,
        ISignalExtractor extractor,
        ICompanyResolver resolver,
        ISignalReviewer reviewer,
        ISignalRepository signalRepository,
        ISignalReviewRepository signalReviewRepository,
        ISignalFileStore signalFileStore,
        ICompanyRepository companyRepository,
        ICollectionHealthValidator healthValidator,
        TimeProvider timeProvider,
        ILogger<CollectionPass> logger,
        IAttentionSourceWeights attentionSourceWeights,
        IDirectionalFilingSignalSource? directionalFilingSignals = null,
        INewsObservationArchive? newsObservationArchive = null,
        NewsObservationCaptureOptions? newsObservationCaptureOptions = null,
        IReportedMetricStore? reportedMetricStore = null,
        IReportedMetricOutbox? reportedMetricOutbox = null,
        IAnalyzedFilingCache? analyzedFilingCache = null)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(rawEvidenceStore);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(signalReviewRepository);
        ArgumentNullException.ThrowIfNull(signalFileStore);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(healthValidator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(attentionSourceWeights);

        // Materialize once in a stable CollectorName-ordinal order so the merge order — and therefore
        // which collector "wins" a ContentHash tie in AddIfNewAsync — is deterministic across runs and
        // independent of DI registration order.
        _collectors = collectors
            .OrderBy(c => c.CollectorName, StringComparer.Ordinal)
            .ToList();

        // Fail fast on an empty enumerable: DI happily supplies zero collectors when none are
        // registered, which would otherwise let the pipeline "succeed" while silently collecting no
        // evidence. This restores the fail-fast guarantee the previous single-collector constructor
        // gave for free.
        if (_collectors.Count == 0)
        {
            throw new ArgumentException(
                "At least one IEvidenceCollector must be registered; the pipeline cannot run with no collectors.",
                nameof(collectors));
        }

        _mapper = mapper;
        _evidenceRepository = evidenceRepository;
        _rawEvidenceStore = rawEvidenceStore;
        _extractor = extractor;
        _resolver = resolver;
        _reviewer = reviewer;
        _signalRepository = signalRepository;
        _signalReviewRepository = signalReviewRepository;
        _signalFileStore = signalFileStore;
        _companyRepository = companyRepository;
        _healthValidator = healthValidator;
        _timeProvider = timeProvider;
        _logger = logger;
        _attentionSourceWeights = attentionSourceWeights;
        _directionalFilingSignals = directionalFilingSignals;
        _newsObservationArchive = newsObservationArchive;
        _newsObservationCaptureOptions = newsObservationCaptureOptions ?? new NewsObservationCaptureOptions();
        _reportedMetricStore = reportedMetricStore;
        _reportedMetricOutbox = reportedMetricOutbox;
        _analyzedFilingCache = analyzedFilingCache;
    }

    /// <summary>The collector names that will run, in the stable order fixed in the constructor.</summary>
    public IReadOnlyList<string> CollectorNames => [.. _collectors.Select(c => c.CollectorName)];

    public async Task<CollectionPassResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var evidenceCollected = 0;
        var evidenceNew = 0;
        var signalsExtracted = 0;
        var signalsValid = 0;
        var signalsApproved = 0;
        var signalsNeedingReview = 0;

        // Spec 193 §1: how many signals this pass held in memory but could NOT durably persist. It is a
        // separate axis from every counter above — a not-persisted signal was still extracted, validated,
        // reviewed and counted as such, because it really was; what it is NOT is in the accrued store.
        var signalsNotPersisted = 0;

        // Spec 206 §3: how many collected items' raw-evidence records did NOT become durable. Null until the
        // first candidate write is attempted — "nothing was collected" and "everything persisted" are
        // different facts, and only the second may claim a measured 0. A Failed item is admitted NOWHERE
        // (not in newEvidence, not extracted, not in the hydrated index), so the next collection in this
        // process retries it naturally.
        int? rawEvidenceNotPersisted = null;

        // Stage 1 + 2: collect raw evidence over the watch universe, map each result to an immutable
        // domain EvidenceItem (normalization, hashing, quality parsing live in the mapper), then
        // dedupe-store. Only newly-stored evidence is extracted so re-collected duplicates never
        // produce duplicate signals. Iterate in the collector's returned (deterministic) order. The
        // companies are loaded once up front: the collection context needs them and Stage 6 reuses
        // the same list for scoring.
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
        var sourceFeeds = await _companyRepository.GetSourceFeedsAsync(ct).ConfigureAwait(false);
        var context = new CollectionContext(companies, sourceFeeds);

        // Collection-health validation (spec 98): reconcile the seed-declared feed-type inventory against
        // what actually reached this context and log any per-feed-type shrinkage (regression guard for the
        // spec-97 feed-Id collision). Diagnostic ONLY — read-only over the already-built context; it never
        // touches a counter, the scoring loop, the evidence/signal path, or asOfUtc, and never fails the run.
        var health = await _healthValidator.ValidateAsync(context, ct).ConfigureAwait(false);
        foreach (var w in health.Warnings)
        {
            _logger.LogWarning("Collection health [{Code}]: {Message}", w.Code, w.Message);
        }

        // Run every registered collector sequentially in the stable order fixed in the constructor
        // (keeps determinism and avoids hammering the network), then merge their results into one. The
        // merge concatenates evidence in collector order without re-sorting/de-duping; cross-collector
        // duplicates resolve downstream via the insert-only ContentHash dedupe (AD-1).
        //
        // COLLECTION PROVENANCE IS STAMPED HERE, AND ONLY HERE (spec 146). CollectionResultMerger.Merge
        // concatenates every collector's evidence into one list and discards per-collector attribution, so
        // the collector that produced an item has to be recorded BEFORE the merge — after it, the
        // information no longer exists. This is the one site that knows both facts, which is why the twelve
        // collectors are untouched. The stamp goes in the free-form metadata bag, which is NOT an input to
        // evidence identity (spec 145: the normalized title+body hash alone) nor to ContentHash, so no
        // evidence id moves, no AddIfNewAsync dedupe decision changes, and no scoring fingerprint moves.
        //
        // PER-COLLECTOR RUN PROVENANCE IS ALSO CAPTURED HERE, AND ONLY HERE (spec 169), for exactly the same
        // structural reason: the merge sums every collector's summary into one aggregate, so "which collector
        // failed, for which company, and could its result have been truncated" has to be recorded before it.
        // AD-16's 2026-08-03 amendment turns on this being a real per-collector/per-company fact rather than
        // an aggregate — an aggregate SourcesFailed cannot separate two failed RSS feeds from one failed
        // newssearch feed. Observational only: hashed into nothing, scored by nothing.
        // NEWS OBSERVATION SIDECARS ARE CAPTURED HERE TOO (spec 177), before the merge, for the same
        // structural reason as coverage/provenance: the merge discards per-collector attribution, and the
        // sidecar's whole value is knowing which collector, feed and query produced each surviving article.
        var results = new List<CollectionResult>(_collectors.Count);
        var collectorRuns = new List<CollectorRunRecord>(_collectors.Count);
        var observationCaptures = new List<(string CollectorName, CollectionResult Result)>();
        foreach (var collector in _collectors)
        {
            ct.ThrowIfCancellationRequested();
            var result = await collector.CollectAsync(context, ct).ConfigureAwait(false);
            results.Add(result with
            {
                Evidence = [.. result.Evidence.Select(
                    e => CollectionProvenanceMetadata.Stamp(e, collector.CollectorName))],
            });

            collectorRuns.Add(BuildCollectorRunRecord(collector.CollectorName, result, health));

            if (result.Observations is not null)
            {
                observationCaptures.Add((collector.CollectorName, result));
            }
        }

        var collected = CollectionResultMerger.Merge(results);
        var newEvidence = new List<CollectedEvidenceEntry>();
        foreach (var item in collected.Evidence)
        {
            ct.ThrowIfCancellationRequested();

            evidenceCollected++;
            var evidence = _mapper.ToEvidenceItem(item);

            // Spec 206 §3 — DURABILITY IS THE ADMISSION DECISION. The insert-only raw store is written
            // FIRST and its typed outcome decides everything downstream: Written means newly DURABLE raw
            // evidence (extract it), AlreadyAvailable means a durable dedupe (cross-run or within-run —
            // skip it, exactly the old AddIfNewAsync dedupe), Failed means the record never became
            // trustworthy on disk, so the item is counted, admitted NOWHERE, and excluded from newEvidence
            // and every extraction/review/signal step. Because a Failed item is never indexed, the next
            // collection IN THIS PROCESS retries it naturally — the pre-206 ordering admitted it to the
            // in-memory index first, which suppressed every same-process retry while signals could still
            // cite evidence absent from the accrued store.
            var rawWrite = await _rawEvidenceStore.WriteIfNewAsync(evidence, ct).ConfigureAwait(false);
            rawEvidenceNotPersisted ??= 0;
            if (rawWrite.Outcome == DurableWriteOutcome.Failed)
            {
                rawEvidenceNotPersisted++;
                continue;
            }

            // Keep the in-process repository in step so scoring can resolve the evidence. In production the
            // repository IS the raw store (spec 142: one FileRawEvidenceStore under both interfaces), so the
            // durable write above already indexed the item and IS the admission — asking the same index
            // again would refuse the item as a duplicate of itself. In a composition where they are separate
            // objects (tests/in-memory), the repository is admitted here and its own insert-only dedupe
            // still gates newness alongside the store's.
            var newlyAdmitted = ReferenceEquals(_evidenceRepository, _rawEvidenceStore)
                ? rawWrite.Outcome == DurableWriteOutcome.Written
                : await _evidenceRepository.AddIfNewAsync(evidence, ct).ConfigureAwait(false)
                    && rawWrite.Outcome == DurableWriteOutcome.Written;
            if (newlyAdmitted)
            {
                // EvidenceNew means newly DURABLE raw evidence (spec 206 §3), not merely admitted to memory.
                evidenceNew++;
                newEvidence.Add(new CollectedEvidenceEntry(evidence, item.CompanyHints));
            }
        }

        // Spec 206 §3: ONE aggregated Warning for the raw-evidence store per pass (the spec-145 aggregation
        // precedent) — the store's own per-file line is demoted to Debug, so this is the one operator signal.
        if (rawEvidenceNotPersisted is > 0)
        {
            _logger.LogWarning(
                "{RawEvidenceNotPersisted} of {EvidenceCollected} collected item(s) this run could NOT be "
                    + "durably persisted to the raw evidence store (the write degraded gracefully). They were "
                    + "EXCLUDED from this run entirely — no extraction, review or signal was produced for "
                    + "them, because a signal must never cite evidence absent from the accrued store — and "
                    + "they were not admitted to the in-memory index, so the next collection (this process "
                    + "included) retries them naturally. The run was not aborted. This Warning is the ONLY "
                    + "report of these failures; raise the raw-evidence-store log level to Debug to see the "
                    + "attempted paths.",
                rawEvidenceNotPersisted,
                evidenceCollected);
        }

        // A single run instant feeds the mapper's createdAtUtc, the scoring windowEndUtc, and the
        // report periodEndUtc so the whole run is internally consistent. TimeProvider.GetUtcNow()
        // already returns a zero-offset DateTimeOffset (the report builder requires zero offset).
        //
        // Captured AFTER collection on purpose: the run instant must not precede the collection that
        // produced this run's evidence. The collector stamps each item's CollectedAtUtc as it reads,
        // so just-collected evidence with no PublishedAtUtc (whose ObservedAtUtc falls back to
        // CollectedAtUtc) would sort just AFTER an earlier asOfUtc and fall outside the (start, end]
        // scoring window — scoring from zero signals in the same run. Capturing here keeps asOfUtc at
        // or after every CollectedAtUtc so freshly collected evidence is in-window.
        var asOfUtc = _timeProvider.GetUtcNow();

        // OPT-IN news observation capture (spec 177): archive the surviving news-search articles and this
        // pass's batch manifest. Runs AFTER the asOfUtc capture (the manifest's run association is that
        // exact instant) and regardless of the evidence-dedupe outcomes above — an accrued AddIfNewAsync
        // duplicate still reaches observation capture, because evidence dedupe and observation capture
        // answer different questions. A write error inside the archive degrades to a Warning recorded on
        // the manifest as unproven capture; it never aborts the run and never changes a counter.
        Guid? newsObservationBatchId = null;
        if (_newsObservationArchive is not null && observationCaptures.Count > 0)
        {
            newsObservationBatchId = await CaptureNewsObservationsAsync(observationCaptures, asOfUtc, ct)
                .ConfigureAwait(false);
        }

        // SPEC 216 §2 — OUTBOX REPLAY, BEFORE any fresh read. A stuck envelope is retried before new work is
        // added, so a ledger write that failed last run cannot be starved by this run's fresh extractions.
        // Replay is a store write and NOTHING else: no www.sec.gov fetch, no model call, no re-derivation —
        // the envelope IS the complete payload, which is exactly why it exists.
        var ledger = new ReportedMetricLedgerTally();
        await ReplayReportedMetricOutboxAsync(ledger, ct).ConfigureAwait(false);

        // OPT-IN directional filing enrichment (AI only). Null when AI is disabled -> skipped entirely, so
        // the default pipeline is byte-for-byte unchanged. Produced BEFORE the deterministic extract loop
        // stores signals (spec 78, Option B: suppress-before-store) so the extract loop knows which
        // filings' deterministic Neutral GuidanceChange to suppress. ProduceAsync has no persistence side
        // effects, and asOfUtc was already captured above, so computing it here does NOT change the run
        // instant or window semantics (AD-7 preserved) — only the storage ordering within the run.
        IReadOnlyList<DirectionalFilingSignal> directional = [];
        var hintsByEvidenceId = new Dictionary<Guid, IReadOnlyList<string>>();
        var supersededFilingEvidenceIds = new HashSet<Guid>();
        if (_directionalFilingSignals is not null)
        {
            var candidates = newEvidence
                .Where(e => e.Evidence.SourceType == EvidenceSourceType.Filing)
                .ToList();

            // Preserve each Filing evidence's collector hints by Id: the source echoes back the SAME
            // EvidenceItem instance it was handed, so directional.Evidence.Id keys straight back to the
            // hints (e.g. ticker) the collector supplied. Threading them into the resolver drives the
            // high-precedence hint path just like the keyword-extraction loop below, instead of forcing
            // every directional signal down the CompanyMention fallback.
            hintsByEvidenceId = candidates.ToDictionary(e => e.Evidence.Id, e => e.CompanyHints);

            // Bail before the (potentially IO-bound: LLM/HTTP) directional read if the run was already
            // cancelled — the deterministic extract loop's first check is below, so without this an
            // already-cancelled run would still pay for ProduceAsync's work.
            ct.ThrowIfCancellationRequested();

            directional = await _directionalFilingSignals
                .ProduceAsync(candidates.Select(e => e.Evidence).ToList(), asOfUtc, ct).ConfigureAwait(false);

            // Supersede key: a directional GuidanceChange REPLACES the deterministic GuidanceChange over
            // the SAME filing evidence. Key on EvidenceId (defensive on Type — every produced directional
            // signal is a GuidanceChange today, but keying on type keeps the supersede exact/future-proof).
            supersededFilingEvidenceIds = directional
                .Where(d => d.Signal.SignalType == nameof(SignalType.GuidanceChange))
                .Select(d => d.Evidence.Id)
                .ToHashSet();
        }
        else
        {
            // SPEC 223 §1/§4 — registration is not evidence of operation, and NON-registration must be
            // stated too. Without the source, no earnings 8-K is read this pass, so nothing can be
            // extracted and the reported-metrics ledger cannot receive anything from this pass. ONE line
            // per pass; a reader must never have to infer "the AI read is off" from the absence of the
            // supply line the source would have emitted.
            _logger.LogInformation(
                "Directional filing read supply: the AI directional filing read is NOT registered this pass "
                    + "(Radar:Ai:Enabled off or no analyzer composed); no earnings 8-K was read, no "
                    + "reported metrics could be extracted, and the reported-metrics ledger cannot receive "
                    + "anything from this pass. {FilingEvidence} Filing evidence item(s) were collected and "
                    + "went to the deterministic extractor only.",
                newEvidence.Count(e => e.Evidence.SourceType == EvidenceSourceType.Filing));
        }

        // Spec 194: there is deliberately NO news-read preparation here. Spec 191 prepared an
        // INewsDirectionalReadSource at this exact point so the extractor could take a news article's
        // DIRECTION from a company judgment — but the stage-2 judge runs AFTER this pass, so the only
        // judgment such a read could ever see was one produced from earlier articles it had never read.
        // Direction now arrives as its own judgment-derived signal, materialized after the judgment exists;
        // ordinary news extraction below is once again the pre-191 Neutral media-attention event.

        // Stage 4 + 3 + 5: extract → resolve → review → store, per new evidence, in order. Each
        // evidence's collector hints (entry.CompanyHints) are passed to the resolver so a
        // company-specific feed's binding can drive a high-confidence resolution. When a confidence-gated
        // directional read superseded this filing's GuidanceChange, the deterministic Neutral is skipped
        // before store (spec 78) — the directional signal carries the filing's GuidanceChange instead.
        foreach (var entry in newEvidence)
        {
            ct.ThrowIfCancellationRequested();

            var evidence = entry.Evidence;
            var output = await _extractor.ExtractAsync(evidence, ct).ConfigureAwait(false);
            foreach (var extracted in output.Signals)
            {
                ct.ThrowIfCancellationRequested();

                if (IsSupersededGuidanceChange(extracted, evidence, supersededFilingEvidenceIds))
                {
                    // A directional filing read supersedes this deterministic Neutral GuidanceChange for
                    // the SAME filing evidence: do NOT store it and do NOT bump any counter (it is
                    // replaced, not dropped-as-invalid). The directional signal below counts instead.
                    _logger.LogDebug(
                        "Suppressing deterministic Neutral GuidanceChange for filing evidence {EvidenceId}: " +
                        "a directional filing read supersedes it.",
                        evidence.Id);
                    continue;
                }

                signalsExtracted++;

                var stored = await MapResolveReviewStoreAsync(
                    extracted, evidence, entry.CompanyHints, asOfUtc, ct).ConfigureAwait(false);
                if (stored.NotPersisted)
                {
                    signalsNotPersisted++;
                }

                switch (stored.Outcome)
                {
                    case SignalStoreOutcome.Approved:
                        signalsValid++;
                        signalsApproved++;
                        break;
                    case SignalStoreOutcome.NeedsReview:
                        signalsValid++;
                        signalsNeedingReview++;
                        break;
                    case SignalStoreOutcome.OtherValid:
                        signalsValid++;
                        break;
                    case SignalStoreOutcome.Dropped:
                        break;
                }
            }
        }

        // Store the directional filing signals (opt-in; empty when AI disabled) through the SAME
        // map -> resolve -> review -> store path as keyword signals (provenance preserved). Each
        // directional GuidanceChange has already superseded the deterministic Neutral over the same
        // filing evidence in the extract loop above.
        //
        // Spec 215 §1: the reported-metrics ledger accounting for THIS run, aggregated into ONE line after
        // the loop (the spec-145 precedent) — never a line per filing. Every extraction a fresh read
        // produced ends in exactly one of: a file written, a file already on disk, a file not persisted,
        // no resolved company to file it under, or no ledger registered — so nothing is discarded uncounted.
        // (The tally was opened before the outbox replay above, so replayed and fresh writes share ONE
        // accounting rather than two that can disagree.)
        foreach (var d in directional)
        {
            ct.ThrowIfCancellationRequested();

            signalsExtracted++;

            // Resolve with the filing evidence's own collector hints when present; an absent entry
            // (defensive — every produced signal's evidence came from candidates) falls back to the
            // empty list, i.e. the CompanyMention (= filing SourceName) path.
            var directionalHints = hintsByEvidenceId.GetValueOrDefault(d.Evidence.Id, []);
            var directionalStored = await MapResolveReviewStoreAsync(
                d.Signal, d.Evidence, directionalHints, asOfUtc, ct).ConfigureAwait(false);
            if (directionalStored.NotPersisted)
            {
                signalsNotPersisted++;
            }

            switch (directionalStored.Outcome)
            {
                case SignalStoreOutcome.Approved:
                    signalsValid++;
                    signalsApproved++;
                    break;
                case SignalStoreOutcome.NeedsReview:
                    signalsValid++;
                    signalsNeedingReview++;
                    break;
                case SignalStoreOutcome.OtherValid:
                    signalsValid++;
                    break;
                case SignalStoreOutcome.Dropped:
                    break;
            }

            if (d.ReportedMetrics is { } extraction)
            {
                await EnqueueAndWriteReportedMetricsAsync(
                    extraction,
                    d.Evidence,
                    directionalStored.CompanyId,
                    directionalStored.CompanyMention,
                    directionalHints,
                    ledger,
                    ct).ConfigureAwait(false);
            }
            else if (d.CachedReportedMetricsPolicy is { } stampedPolicy && d.Accession is { } stampedAccession)
            {
                // SPEC 216 §2 — the 215-era shape, FAILING CLOSED. A cache record carrying a policy stamp
                // with NO outbox envelope behind it means the extraction was made and lost before it
                // reached the ledger. It is NOT acknowledged as "extracted, nothing verified": it is
                // counted, named once per accession, and recoverable only by a conscious maintainer
                // re-analysis (the stamp is honoured as a HIT, so nothing here spends a model call).
                await CheckReportedMetricsPolicyStampAsync(stampedPolicy, stampedAccession, ledger, ct)
                    .ConfigureAwait(false);
            }
        }

        // SPEC 223 §2 — ONE aggregated Information line per pass, on EVERY pass, all-zero included (the
        // spec-145 aggregation precedent). The pre-223 activity gate made an ENABLED ledger that had never
        // written anything indistinguishable from a broken one, a disabled one, and one whose output was
        // dropped; a silence is not a measured zero. The line therefore states the POLICY in force
        // (ENABLED = store + outbox registered, DISABLED = not registered, and the `rm=` token the
        // directional-filing descriptor carries for that state), the spec's named YIELD counters, and the
        // ACCRUED inventory on disk — so "never written anything" is visible without a filesystem check.
        // A count the store could not establish renders as "not recorded (<reason>)", never as 0.
        var ledgerRegistered = _reportedMetricStore is not null;
        var ledgerInventory = await InventoryReportedMetricLedgerAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Reported-metrics ledger ({State}; rm={PolicyToken}): {Extractions} fresh earnings read(s) extracted metrics; "
                + "ledger files written {Written} / already on disk {AlreadyOnDisk} / not persisted "
                + "{NotPersisted} / no resolved company {NoCompany} / no ledger registered {NoStore}; "
                + "records written {Records}; metrics dropped unverified {Unverified} / unrecognised "
                + "{Unrecognised} / duplicate {Duplicate} / metric-not-in-quote {MetricNotInQuote} / "
                + "period-not-in-quote {PeriodNotInQuote} / fragment {Fragment} / not-associated "
                + "{NotAssociated}; prior pairs dropped incomplete {IncompletePriorPairs}. "
                + "Outbox pending {OutboxPending} (of which not acknowledged "
                + "{OutboxNotAcknowledged}) / replayed {OutboxReplayed} / acknowledged "
                + "{OutboxAcknowledged} / attempts-exhausted {OutboxAttemptsExhausted} / not enqueued "
                + "{OutboxNotEnqueued} / unresolved company {OutboxUnresolved} / attempt updates not "
                + "persisted {OutboxAttemptUpdatesNotPersisted} / cache stamps written (verified by "
                + "re-read) {CacheStamps} / not written {CacheStampsNotWritten}; policy stamp with no "
                + "envelope {PolicyStampWithoutEnvelope} / not checked {PolicyStampNotChecked}. "
                + "Yield this pass: ReportedMetricsExtracted {ReportedMetricsExtracted} / "
                + "OutboxPayloadsEnqueued {OutboxPayloadsEnqueued} / OutboxWritesSucceeded "
                + "{OutboxWritesSucceeded} (ledger files written + already on disk) / OutboxWritesRetried "
                + "{OutboxWritesRetried} (outbox replays) / LedgerEntriesWritten {LedgerEntriesWritten} "
                + "(records written). Accrued: LedgerEntriesOnDisk {LedgerEntriesOnDisk}.",
            ledgerRegistered ? "ENABLED" : "DISABLED",
            ledgerRegistered ? ReportedMetricsPolicy.Version : ReportedMetricsPolicy.DisabledToken,
            ledger.Extractions,
                ledger.FilesWritten,
                ledger.FilesAlreadyOnDisk,
                ledger.FilesNotPersisted,
                ledger.NoResolvedCompany,
                ledger.NoStoreRegistered,
                ledger.RecordsWritten,
                ledger.DroppedUnverified,
                ledger.DroppedUnrecognised,
                ledger.DroppedDuplicate,
                ledger.DroppedMetricNotInQuote,
                ledger.DroppedPeriodNotInQuote,
                ledger.DroppedFragment,
                ledger.DroppedNotAssociated,
                ledger.PriorPairsDroppedIncomplete,
                ledger.OutboxPending,
                ledger.OutboxNotAcknowledged,
                ledger.OutboxReplayed,
                ledger.OutboxAcknowledged,
                ledger.OutboxAttemptsExhausted,
                ledger.OutboxNotEnqueued,
                ledger.OutboxUnresolvedCompany,
                ledger.OutboxAttemptUpdatesNotPersisted,
                ledger.CacheStampsWritten,
                ledger.CacheStampsNotWritten,
                ledger.PolicyStampWithoutEnvelope,
                ledger.PolicyStampNotChecked,
                // SPEC 223 §2 — the spec-223 yield names are MAPPED onto the spec-216 counters the pass
                // already keeps, NOT measured separately: ReportedMetricsExtracted = Extractions,
                // OutboxPayloadsEnqueued = OutboxEnqueued, OutboxWritesSucceeded = FilesWritten +
                // FilesAlreadyOnDisk (a ledger file reached disk from an envelope, whether this pass wrote
                // it or found it), OutboxWritesRetried = OutboxReplayed (an envelope re-driven from the
                // outbox on a later pass), LedgerEntriesWritten = RecordsWritten. There is no distinct
                // counter behind any of the five; the line names the mapping in its own text too.
                ledger.Extractions,
                ledger.OutboxEnqueued,
                ledger.FilesWritten + ledger.FilesAlreadyOnDisk,
                ledger.OutboxReplayed,
                ledger.RecordsWritten,
                RenderLedgerInventory(ledgerInventory, ledgerRegistered));

        // Spec 193 §1: ONE aggregated Warning per store per run (the spec-145 aggregation precedent), never
        // one line per failure — a bad disk would otherwise bury the run log in thousands of identical lines.
        if (signalsNotPersisted > 0)
        {
            _logger.LogWarning(
                "{SignalsNotPersisted} signal(s) this run could NOT be durably persisted to the signal "
                    + "store. They are in this process's in-memory index and were scored by this run, but "
                    + "nothing reached disk: the accrued signal history does NOT contain them and the next "
                    + "run's history read will not see them. The run was not aborted. This Warning is the "
                    + "ONLY report of these failures (spec 195 §1): the store no longer logs a Warning per "
                    + "failed file, so raise the signal-store log level to Debug to see the attempted "
                    + "paths.",
                signalsNotPersisted);
        }

        return new CollectionPassResult(
            AsOfUtc: asOfUtc,
            EvidenceCollected: evidenceCollected,
            EvidenceNew: evidenceNew,
            SignalsExtracted: signalsExtracted,
            SignalsValid: signalsValid,
            SignalsApproved: signalsApproved,
            SignalsNeedingReview: signalsNeedingReview,
            Collection: collected.Summary,
            Health: health,
            Collectors: CollectorNames,
            Companies: companies,
            CollectorRuns: collectorRuns,
            NewsObservationBatchId: newsObservationBatchId,
            SignalsNotPersisted: signalsNotPersisted,
            RawEvidenceNotPersisted: rawEvidenceNotPersisted);
    }

    /// <summary>
    /// Archives every collector-supplied observation candidate and writes the pass's batch manifest
    /// (spec 177 §§3–5). Identity is minted here through <see cref="NewsObservationRecord.Prospective"/> —
    /// the collectors hand over raw provider payloads and never touch a filesystem store. Returns the batch
    /// id, which the run record carries as the EXPLICIT manifest↔run association (never a time join).
    /// <para>
    /// The archive's per-record contract is no-throw (every failure is a typed outcome), so a failure here
    /// can only surface as <c>ObservationsFailed</c> on the manifest — unproven capture for this run, never
    /// an aborted scoring. The manifest write itself degrades the same way (logged Warning, batch id still
    /// recorded so the gap is visible as a dangling id rather than an absent association).
    /// </para>
    /// </summary>
    private async Task<Guid> CaptureNewsObservationsAsync(
        IReadOnlyList<(string CollectorName, CollectionResult Result)> captures,
        DateTimeOffset asOfUtc,
        CancellationToken ct)
    {
        var attempted = 0;
        var written = 0;
        var deduped = 0;
        var failed = 0;

        // Spec 196 §3, the CAPTURE-FLOW diagnostic. Every candidate ATTEMPTED is tallied — written,
        // cross-run deduped and failed alike — so the tier counts partition ObservationsAttempted exactly.
        // Resolution goes through the SAME IAttentionSourceWeights the score consumes, so the diagnostic can
        // never disagree with the map it is describing; and it uses Resolve rather than WeightFor because
        // since the spec-196 inversion an explicit Mill and an unclassified publisher share one weight.
        var observationsByTier = new Dictionary<string, int>(StringComparer.Ordinal);

        // Case-insensitive GROUPING (one outlet, however its feed capitalised it that day) but a
        // deterministic DISPLAY spelling (AD-3): the raw spellings are folded onto the ordinally-smallest
        // one seen, never onto whichever arrived first. Keying on the raw string alone would make the
        // rendered name — and therefore the ordering tie-break and the log line — depend on collector
        // iteration order.
        var unclassifiedByPublisher =
            new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, result) in captures)
        {
            foreach (var candidate in result.Observations!)
            {
                ct.ThrowIfCancellationRequested();
                attempted++;

                var resolution = _attentionSourceWeights.Resolve(candidate.Publisher);
                observationsByTier[resolution.TierName] =
                    observationsByTier.GetValueOrDefault(resolution.TierName) + 1;
                if (!resolution.IsExplicitlyMapped)
                {
                    var publisher = string.IsNullOrWhiteSpace(candidate.Publisher)
                        ? UnclassifiedPublisherCoverage.Unattributed
                        : candidate.Publisher.Trim();
                    unclassifiedByPublisher[publisher] =
                        unclassifiedByPublisher.TryGetValue(publisher, out var seen)
                            ? (string.CompareOrdinal(publisher, seen.Display) < 0 ? publisher : seen.Display,
                                seen.Count + 1)
                            : (publisher, 1);
                }

                var outcome = await _newsObservationArchive!
                    .WriteAsync(NewsObservationRecord.Prospective(candidate), ct)
                    .ConfigureAwait(false);
                switch (outcome)
                {
                    case NewsObservationWriteOutcome.Written:
                        written++;
                        break;
                    case NewsObservationWriteOutcome.CrossRunDeduped:
                        deduped++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }
        }

        var attentionCoverage = BuildAttentionPublisherCoverage(
            attempted, observationsByTier, unclassifiedByPublisher);

        var batch = new NewsObservationBatch(
            BatchId: Guid.NewGuid(),
            RunAsOfUtc: asOfUtc,
            SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
            FullUniverse: _newsObservationCaptureOptions.FullUniverse,
            ObservationsAttempted: attempted,
            ObservationsWritten: written,
            ObservationsCrossRunDeduped: deduped,
            ObservationsFailed: failed,
            CaptureProven: failed == 0,
            Collectors:
            [
                .. captures.Select(c => new NewsObservationCollectorCapture(
                    CollectorName: c.CollectorName,
                    CompanyCoverage: c.Result.CompanyCoverage,
                    ProviderFailures: c.Result.Summary.Failures,
                    // LEGACY COMPATIBILITY MIRROR (spec 190): the member is historically misnamed and
                    // non-nullable, so it keeps carrying the EFFECTIVE/LOCAL-limit fact verbatim for readers
                    // written before the rename. It is NOT evidence about provider behaviour.
                    AnyFeedHitProviderCap:
                        c.Result.CompanyCoverage?.Any(cov => cov.HitEffectiveResultLimit) ?? false,
                    AnyFeedHitEffectiveResultLimit:
                        c.Result.CompanyCoverage?.Any(cov => cov.HitEffectiveResultLimit),
                    AnyFeedConfirmedLocalTruncation:
                        AnyConfirmedLocalTruncation(c.Result.CompanyCoverage))),
            ],
            AttentionPublisherCoverage: attentionCoverage);

        if (failed > 0)
        {
            _logger.LogWarning(
                "News observation capture is UNPROVEN for this run: {Failed} of {Attempted} observation(s) "
                    + "could not be archived (batch {BatchId}). A later reader must treat this run as "
                    + "unknown coverage, never as a clean zero.",
                failed,
                attempted,
                batch.BatchId);
        }

        var manifestWritten = await _newsObservationArchive!.WriteBatchAsync(batch, ct).ConfigureAwait(false);
        if (!manifestWritten)
        {
            _logger.LogWarning(
                "News observation batch manifest {BatchId} could not be written; this run's capture is "
                    + "unproven (the run record still carries the batch id, so the gap stays visible).",
                batch.BatchId);
        }

        _logger.LogInformation(
            "News observation capture complete (batch {BatchId}): {Attempted} attempted, {Written} written, "
                + "{Deduped} cross-run deduped, {Failed} failed.",
            batch.BatchId,
            attempted,
            written,
            deduped,
            failed);

        LogAttentionPublisherCoverage(batch.BatchId, attentionCoverage);

        return batch.BatchId;
    }

    /// <summary>
    /// Projects the per-tier / per-unclassified-publisher tallies into the spec-196 capture-flow summary.
    /// Ordering is deterministic (AD-3): descending count, then name (ordinal) — so a re-run over the same
    /// candidates renders the same document. The unclassified sentinel is a tier ROW, which is what makes
    /// "the tier counts sum to ObservationsAttempted" exactly true rather than true-after-adding-a-remainder.
    /// </summary>
    private static AttentionPublisherCoverageSummary BuildAttentionPublisherCoverage(
        int attempted,
        IReadOnlyDictionary<string, int> observationsByTier,
        IReadOnlyDictionary<string, (string Display, int Count)> unclassifiedByPublisher) =>
        new(
            Version: AttentionPublisherCoverageSummary.CurrentVersion,
            ObservationsAttempted: attempted,
            Tiers:
            [
                .. observationsByTier
                    .OrderByDescending(e => e.Value)
                    .ThenBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => new AttentionPublisherTierCoverage(e.Key, e.Value)),
            ],
            DistinctUnclassifiedPublishers: unclassifiedByPublisher.Count,
            TopUnclassifiedPublishers:
            [
                .. unclassifiedByPublisher.Values
                    .OrderByDescending(e => e.Count)
                    .ThenBy(e => e.Display, StringComparer.Ordinal)
                    .Take(AttentionPublisherCoverageSummary.TopUnclassifiedPublisherLimit)
                    .Select(e => new UnclassifiedPublisherCoverage(e.Display, e.Count)),
            ]);

    /// <summary>
    /// ONE aggregated Information line per run for the whole publisher-coverage summary (the spec-145
    /// aggregation precedent) — never one line per publisher. It names the tier shares INCLUDING
    /// unclassified and the largest unclassified publishers, so the curation gap is a number someone sees
    /// rather than something discovered by asking why a familiar company scored 75. It states what it is:
    /// a capture-flow diagnostic, not the attention input.
    /// </summary>
    private void LogAttentionPublisherCoverage(Guid batchId, AttentionPublisherCoverageSummary coverage)
    {
        var tiers = coverage.ObservationsAttempted == 0
            ? "(no candidates attempted)"
            : string.Join(
                ", ",
                coverage.Tiers.Select(t => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{t.TierName} {t.Observations} ({(double)t.Observations / coverage.ObservationsAttempted:P1})")));

        var unclassified = coverage.TopUnclassifiedPublishers.Count == 0
            ? "(none)"
            : string.Join(
                ", ",
                coverage.TopUnclassifiedPublishers.Select(p => $"{p.Publisher} {p.Observations}"));

        _logger.LogInformation(
            "Attention publisher coverage for batch {BatchId} ({Version}): {Attempted} attempted candidate(s) "
                + "by tier — {Tiers}. {DistinctUnclassified} distinct unclassified publisher(s); top by volume: "
                + "{TopUnclassified}. CAPTURE-FLOW DIAGNOSTIC ONLY — scoring consumes tier-weighted DISTINCT "
                + "publishers per company over the scoring window, not this candidate volume, and nothing is "
                + "auto-classified from it.",
            batchId,
            coverage.Version,
            coverage.ObservationsAttempted,
            tiers,
            coverage.DistinctUnclassifiedPublishers,
            unclassified);
    }

    /// <summary>
    /// The spec-190 confirmed-local-truncation aggregate over one collector's coverage rows: <c>true</c> when
    /// any row confirms it, <c>false</c> when at least one row RECORDED the diagnostic and none confirmed it,
    /// and <c>null</c> when no row recorded it at all. The <c>null</c> branch is the point — "not recorded"
    /// must never be rendered as "no truncation".
    /// </summary>
    private static bool? AnyConfirmedLocalTruncation(IReadOnlyList<CollectorCompanyCoverage>? coverage) =>
        coverage is { Count: > 0 } rows && rows.Any(r => r.ConfirmedLocalTruncation.HasValue)
            ? rows.Any(r => r.ConfirmedLocalTruncation == true)
            : null;

    /// <summary>
    /// Projects one collector's UNMERGED <see cref="CollectionResult"/> into the durable
    /// <see cref="CollectorRunRecord"/> the run log persists (spec 169), amending its per-company coverage
    /// with the run-level collection-health finding the collector itself cannot see.
    /// <para>
    /// The health report is reconciled over the whole <see cref="CollectionContext"/> (spec 98), one entry per
    /// FEED TYPE — a collector is handed the context, not the reconciliation — so
    /// <see cref="CollectionCoverageIssues.CollectionHealthMismatch"/> can only be stamped here. It is applied
    /// to EVERY company row of the affected collector, because a feed lost between seed and collection is a
    /// statement about the inventory this collector was given, and Radar cannot tell which company's feed went
    /// missing. Over-marking is the safe direction: it costs coverage, it never invents it.
    /// </para>
    /// <para>
    /// The feed-type match is the collector's own <c>CollectorName</c> against
    /// <see cref="CollectionHealthWarning.FeedType"/>, case-insensitively, mirroring how the validator groups
    /// feed types. That is exact for <c>newssearch</c> — the one collector this contract currently matters
    /// for, whose provenance name IS its feed type — and simply never matches for collectors whose name and
    /// feed type differ, which leaves their (already <c>null</c>) coverage untouched.
    /// </para>
    /// </summary>
    private static CollectorRunRecord BuildCollectorRunRecord(
        string collectorName, CollectionResult result, CollectionHealthReport health)
    {
        var summary = result.Summary;
        var coverage = result.CompanyCoverage;

        if (coverage is { Count: > 0 }
            && health.Warnings.Any(w => string.Equals(
                w.FeedType, collectorName, StringComparison.OrdinalIgnoreCase)))
        {
            coverage =
            [
                .. coverage.Select(c => c with
                {
                    Issues = CollectionCoverageIssues.Canonicalize(
                        [.. c.Issues, CollectionCoverageIssues.CollectionHealthMismatch]),
                }),
            ];
        }

        return new CollectorRunRecord(
            CollectorName: collectorName,
            SourcesChecked: summary.SourcesChecked,
            SourcesSucceeded: summary.SourcesSucceeded,
            SourcesFailed: summary.SourcesFailed,
            ItemsCollected: summary.ItemsCollected,
            Failures: summary.Failures,
            CompanyCoverage: coverage);
    }

    /// <summary>
    /// The shared map -&gt; resolve -&gt; review -&gt; store -&gt; file tail used by BOTH the deterministic
    /// keyword extract loop and the opt-in directional filing enrichment. The mapper owns the provenance
    /// check (excerpt must be found in the evidence) and validation — the pass does not re-validate.
    /// Returns which counters the caller should bump (kept in the caller so the run-summary locals stay in
    /// one place), INCLUDING whether the durable mirror write failed (spec 193 §1).
    /// </summary>
    private async Task<SignalStoreResult> MapResolveReviewStoreAsync(
        ExtractedSignal extracted,
        EvidenceItem evidence,
        IReadOnlyList<string> companyHints,
        DateTimeOffset asOfUtc,
        CancellationToken ct)
    {
        var mapping = ExtractedSignalMapper.ToSignal(extracted, evidence, asOfUtc);
        if (!mapping.IsValid)
        {
            _logger.LogDebug(
                "Dropping invalid extracted signal for evidence {EvidenceId}: {Errors}",
                evidence.Id,
                string.Join("; ", mapping.Errors));
            return SignalStoreResult.Of(SignalStoreOutcome.Dropped);
        }

        var signal = mapping.Signal!;

        // Resolve: only ADD a CompanyId when matched; never guess. An unresolved mention
        // stays CompanyId == null and the reviewer routes it to human review.
        var resolution = await _resolver
            .ResolveAsync(signal.CompanyMention, companyHints, ct).ConfigureAwait(false);
        if (resolution.CompanyId is { } companyId)
        {
            signal = signal with { CompanyId = companyId };
        }

        // Review may only lower confidence and set the review status.
        var outcome = await _reviewer.ReviewAsync(signal, evidence, ct).ConfigureAwait(false);

        // Store the reviewed signal, then its immutable audit record alongside it. Provenance
        // holds because outcome.Review.SignalId == outcome.ReviewedSignal.Id (the reviewer
        // builds the review from signal.Id), so the persisted review traces to the stored signal.
        await _signalRepository.AddAsync(outcome.ReviewedSignal, ct).ConfigureAwait(false);
        await _signalReviewRepository.AddAsync(outcome.Review, ct).ConfigureAwait(false);

        // Mirror the stored signal + its review to the on-disk signal store (AD-8), the
        // durable twin of the in-memory repositories. Signals are upsert-by-Id (the store
        // overwrites last-write-wins).
        //
        // SPEC 193 §1 CORRECTS WHAT THIS COMMENT USED TO SAY. It used to read "the store swallows disk
        // errors, so this must not change any counter" — which sanctioned reporting a signal that never
        // reached disk as stored. The store still degrades gracefully and still does not abort the run, and
        // the extract/valid/approved/needs-review counters above are still unaffected (the signal really was
        // extracted, validated and reviewed). What changed: a FAILED durable write IS now counted, on its
        // own axis, and reported in ONE aggregated Warning plus the run record and summary line. Nothing is
        // retried or queued (out of scope) — the failure is recorded, not repaired.
        var durable = await _signalFileStore
            .WriteAsync(outcome.ReviewedSignal, outcome.Review, ct).ConfigureAwait(false);

        var storeOutcome = outcome.ReviewedSignal.ReviewStatus switch
        {
            SignalReviewStatus.Approved => SignalStoreOutcome.Approved,
            SignalReviewStatus.NeedsHumanReview or SignalReviewStatus.Pending => SignalStoreOutcome.NeedsReview,
            _ => SignalStoreOutcome.OtherValid,
        };

        return SignalStoreResult.Of(
            storeOutcome,
            durable.Outcome == DurableWriteOutcome.Failed,
            outcome.ReviewedSignal.CompanyId,
            outcome.ReviewedSignal.CompanyMention);
    }

    /// <summary>
    /// SPEC 216 §2 — replays every PENDING outbox envelope into the ledger BEFORE this pass reads anything
    /// fresh. Each envelope is the COMPLETE ready-to-write payload, so a replay is one
    /// <see cref="IReportedMetricStore.WriteIfNewAsync"/> call: no SEC fetch, no model call, no direction
    /// re-derivation (asserted by a test that supplies a throwing analyzer). On a durable write the
    /// envelope is acknowledged and MOVED to the append-only acknowledged area; otherwise its PERSISTED
    /// attempt count is advanced so the three-attempt warning survives restarts.
    /// <para>
    /// An envelope whose company could not be resolved at enqueue time is re-run through company
    /// resolution here — resolution may succeed later (a universe addition, a hint fix) — and re-enqueued
    /// under the company once it does, with the unresolved copy acknowledged.
    /// </para>
    /// </summary>
    private async Task ReplayReportedMetricOutboxAsync(ReportedMetricLedgerTally ledger, CancellationToken ct)
    {
        if (_reportedMetricOutbox is null)
        {
            return;
        }

        var pending = await _reportedMetricOutbox.EnumeratePendingAsync(ct).ConfigureAwait(false);
        foreach (var envelope in pending)
        {
            ct.ThrowIfCancellationRequested();

            var current = envelope;
            if (current.CompanyId is null)
            {
                ledger.OutboxUnresolvedCompany++;
                var resolution = await _resolver
                    .ResolveAsync(current.CompanyMention, current.CompanyHints, ct).ConfigureAwait(false);
                if (resolution.CompanyId is not { } resolvedNow)
                {
                    // Still unresolved: count the attempt, warn ONCE per accession at the bound, and leave
                    // it pending. Never dropped, never retried silently forever.
                    await AdvanceOutboxAttemptAsync(current, ledger, ct).ConfigureAwait(false);
                    continue;
                }

                // Resolved at last: re-enqueue under the company, then acknowledge the unresolved copy so
                // the chain stays walkable and the envelope is never replayed from two places.
                var reEnqueued = current with { CompanyId = resolvedNow };
                var enqueue = await _reportedMetricOutbox
                    .EnqueueAsync(reEnqueued, ct).ConfigureAwait(false);
                if (!enqueue.Written)
                {
                    ledger.OutboxNotEnqueued++;
                    await AdvanceOutboxAttemptAsync(current, ledger, ct).ConfigureAwait(false);
                    continue;
                }

                // Spec 223 §2: a durable enqueue is counted on its own axis (OutboxPayloadsEnqueued).
                ledger.OutboxEnqueued++;

                if (await _reportedMetricOutbox.AcknowledgeAsync(current, ct).ConfigureAwait(false))
                {
                    ledger.OutboxAcknowledged++;
                }
                else
                {
                    // The re-enqueued copy IS durable, but the unresolved one could not be acknowledged, so
                    // it stays on disk and will be replayed (EnqueueAsync is insert-if-new, so the replay
                    // is idempotent - never a second ledger row). Counted on the same pending axis as the
                    // other two acknowledge sites AND on its own cause axis, so an acknowledge that keeps
                    // failing is visible rather than hidden inside a pending count. The containment is
                    // DISCLOSED in the rendered line ("pending N (of which not acknowledged M)"), so a log
                    // reader can never read the two axes as disjoint envelope counts.
                    ledger.OutboxPending++;
                    ledger.OutboxNotAcknowledged++;
                }

                current = reEnqueued;
            }

            ledger.OutboxReplayed++;
            if (await WriteEnvelopeToLedgerAsync(current, ledger, ct).ConfigureAwait(false))
            {
                if (await _reportedMetricOutbox.AcknowledgeAsync(current, ct).ConfigureAwait(false))
                {
                    ledger.OutboxAcknowledged++;
                }
                else
                {
                    ledger.OutboxPending++;
                    ledger.OutboxNotAcknowledged++;
                }

                continue;
            }

            await AdvanceOutboxAttemptAsync(current, ledger, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records ONE failed attempt against the PERSISTED envelope and, at the third, emits exactly one
    /// Warning per accession per run naming it as a durable defect. The count is read from disk, so it
    /// survives restarts; a store that could not record the attempt is itself counted.
    /// </summary>
    private async Task AdvanceOutboxAttemptAsync(
        ReportedMetricOutboxEnvelope envelope, ReportedMetricLedgerTally ledger, CancellationToken ct)
    {
        ledger.OutboxPending++;

        var updated = await _reportedMetricOutbox!
            .MarkAttemptAsync(envelope, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        var attempts = updated?.Attempts ?? envelope.Attempts + 1;
        if (updated is null)
        {
            ledger.OutboxAttemptUpdatesNotPersisted++;
        }

        if (attempts >= OutboxAttemptWarningThreshold)
        {
            ledger.OutboxAttemptsExhausted++;
            _logger.LogWarning(
                "Reported-metrics outbox envelope for accession {Accession} (policy {Policy}, company "
                    + "{CompanyId}) has now failed {Attempts} ledger attempt(s). It is STILL PENDING and will "
                    + "be replayed — nothing is dropped — but a persistent failure here means the company's "
                    + "reported metrics are not reaching the ledger and the judge sees no reference value "
                    + "for them.",
                envelope.Accession,
                envelope.Policy,
                envelope.CompanyId,
                attempts);
        }
    }

    /// <summary>
    /// Writes ONE envelope's records to the ledger and tallies the outcome. Returns true when the records
    /// are DURABLE (written now or already there) — the only condition under which the envelope may be
    /// acknowledged.
    /// </summary>
    private async Task<bool> WriteEnvelopeToLedgerAsync(
        ReportedMetricOutboxEnvelope envelope, ReportedMetricLedgerTally ledger, CancellationToken ct)
    {
        if (_reportedMetricStore is null)
        {
            ledger.NoStoreRegistered++;
            return false;
        }

        var records = envelope.ToRecords();
        if (records is null)
        {
            // Defensive: the caller resolves the company before reaching here, so this is unreachable in
            // the shipped path — but an unresolved envelope must never be acknowledged as filed.
            ledger.NoResolvedCompany++;
            return false;
        }

        var write = await _reportedMetricStore
            .WriteIfNewAsync(envelope.CompanyId!.Value, envelope.Accession, envelope.Policy, records, ct)
            .ConfigureAwait(false);
        switch (write.Outcome)
        {
            case DurableWriteOutcome.Written:
                ledger.FilesWritten++;
                ledger.RecordsWritten += records.Count;
                return true;
            case DurableWriteOutcome.AlreadyAvailable:
                ledger.FilesAlreadyOnDisk++;
                return true;
            default:
                ledger.FilesNotPersisted++;
                return false;
        }
    }

    /// <summary>
    /// SPEC 216 §2 — the ORDERING that makes a lost extraction impossible, for ONE fresh earnings read:
    /// company already resolved (by the shared map→resolve→review→store tail) → OUTBOX ENVELOPE WRITTEN
    /// (durable) → only then the analyzed-filing record stamped <c>reportedMetricsPolicy</c> → ledger write
    /// from the envelope → acknowledge.
    /// <para>
    /// If the process dies before the envelope is durable, the cache record stays UNSTAMPED and the filing
    /// re-analyzes next run exactly as any uncached read does — nothing was ever persisted to lose, and the
    /// direction of that re-read is the direction, as for every first read. If it dies after, the envelope
    /// replays. An unresolved company is not a loss either: the envelope is enqueued with a null company
    /// and re-run through resolution on every replay.
    /// </para>
    /// Never throws for a store failure (every seam returns a typed outcome); only caller cancellation
    /// propagates.
    /// </summary>
    private async Task EnqueueAndWriteReportedMetricsAsync(
        ReportedMetricExtraction extraction,
        EvidenceItem evidence,
        Guid? companyId,
        string? companyMention,
        IReadOnlyList<string> companyHints,
        ReportedMetricLedgerTally ledger,
        CancellationToken ct)
    {
        ledger.Extractions++;
        ledger.DroppedUnverified += extraction.Metrics.DroppedUnverified;
        ledger.DroppedUnrecognised += extraction.Metrics.DroppedUnrecognised;
        ledger.DroppedDuplicate += extraction.Metrics.DroppedDuplicate;
        ledger.PriorPairsDroppedIncomplete += extraction.Metrics.PriorPairsDroppedIncomplete;
        ledger.DroppedMetricNotInQuote += extraction.Metrics.DroppedMetricNotInQuote;
        ledger.DroppedPeriodNotInQuote += extraction.Metrics.DroppedPeriodNotInQuote;
        ledger.DroppedFragment += extraction.Metrics.DroppedFragment;
        ledger.DroppedNotAssociated += extraction.Metrics.DroppedNotAssociated;

        if (_reportedMetricOutbox is null || _reportedMetricStore is null)
        {
            ledger.NoStoreRegistered++;
            return;
        }

        var policy = ReportedMetricsPolicy.Version;
        var envelope = new ReportedMetricOutboxEnvelope(
            OutboxId: ReportedMetricOutboxEnvelope.IdentityFor(policy, extraction.Accession),
            Policy: policy,
            CompanyId: companyId,
            CompanyMention: companyMention ?? string.Empty,
            CompanyHints: companyHints,
            EvidenceId: evidence.Id,
            Accession: extraction.Accession,
            Form: extraction.Form,
            FilingDateUtc: evidence.PublishedAtUtc ?? evidence.CollectedAtUtc,
            ReaderIdentity: extraction.ReaderIdentity,
            Metrics: extraction.Metrics.Verified,
            DroppedUnrecognised: extraction.Metrics.DroppedUnrecognised,
            DroppedUnverified: extraction.Metrics.DroppedUnverified,
            DroppedDuplicate: extraction.Metrics.DroppedDuplicate,
            PriorPairsDroppedIncomplete: extraction.Metrics.PriorPairsDroppedIncomplete,
            DroppedMetricNotInQuote: extraction.Metrics.DroppedMetricNotInQuote,
            DroppedPeriodNotInQuote: extraction.Metrics.DroppedPeriodNotInQuote,
            DroppedFragment: extraction.Metrics.DroppedFragment,
            DroppedNotAssociated: extraction.Metrics.DroppedNotAssociated,
            State: ReportedMetricOutboxState.Pending,
            Attempts: 0,
            CreatedAtUtc: _timeProvider.GetUtcNow(),
            LastAttemptAtUtc: null);

        var enqueue = await _reportedMetricOutbox.EnqueueAsync(envelope, ct).ConfigureAwait(false);
        if (!enqueue.Written)
        {
            // THE ENVELOPE IS THE PRECONDITION. Nothing is stamped and nothing is written: the filing
            // re-analyzes next run, which is the correct outcome because nothing was persisted to lose.
            ledger.OutboxNotEnqueued++;
            return;
        }

        // Spec 223 §2: a durable enqueue is counted on its own axis (OutboxPayloadsEnqueued).
        ledger.OutboxEnqueued++;

        // Only NOW may the cache say "extraction done under this policy" — the meaning of that stamp is
        // exactly "an outbox envelope exists for this accession under this policy".
        await StampReportedMetricsPolicyAsync(extraction.Accession, policy, ledger, ct).ConfigureAwait(false);

        if (companyId is null)
        {
            // Routable, not lost: the envelope sits in the unresolved area and every later pass re-runs it
            // through company resolution.
            ledger.NoResolvedCompany++;
            ledger.OutboxUnresolvedCompany++;
            ledger.OutboxPending++;
            return;
        }

        if (await WriteEnvelopeToLedgerAsync(envelope, ledger, ct).ConfigureAwait(false))
        {
            if (await _reportedMetricOutbox.AcknowledgeAsync(envelope, ct).ConfigureAwait(false))
            {
                ledger.OutboxAcknowledged++;
            }
            else
            {
                ledger.OutboxPending++;
                ledger.OutboxNotAcknowledged++;
            }

            return;
        }

        await AdvanceOutboxAttemptAsync(envelope, ledger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stamps the analyzed-filing cache record for <paramref name="accession"/> with
    /// <paramref name="policy"/> (spec 216 §2). A missing cache, an absent record or a lookup failure is
    /// COUNTED, never silent: the consequence is only that the filing re-analyzes on a later run, which is
    /// the same fail-safe every uncached read already has.
    /// </summary>
    private async Task StampReportedMetricsPolicyAsync(
        string accession, string policy, ReportedMetricLedgerTally ledger, CancellationToken ct)
    {
        if (_analyzedFilingCache is null)
        {
            ledger.CacheStampsNotWritten++;
            return;
        }

        try
        {
            var record = await _analyzedFilingCache.TryGetAsync(accession, ct).ConfigureAwait(false);
            if (record is null)
            {
                ledger.CacheStampsNotWritten++;
                return;
            }

            await _analyzedFilingCache
                .PutAsync(record with { ReportedMetricsPolicy = policy }, ct)
                .ConfigureAwait(false);

            // MEASURE THE STAMP; DO NOT ASSUME IT. PutAsync returns a bare Task and the file cache
            // discards its writer's bool, so counting a stamp here on the strength of the call returning
            // would report "cache stamps written 1 / not written 0" while a gracefully-degraded disk wrote
            // nothing - the discarded-bool / "failed durable write reported as stored" defect specs 192
            // and 193 closed elsewhere. The cache has no in-memory layer (TryGetAsync reads the file every
            // time), so re-reading the record and checking the policy is ACTUALLY present is a real
            // verification, not a tautology. Cost is one extra cache read per fresh extraction, bounded by
            // MaxFilingsPerRun. A stamp that did not land is counted as NOT written, which is exactly what
            // it is; the consequence stays the benign one (the filing re-analyzes on a later run).
            var stamped = await _analyzedFilingCache.TryGetAsync(accession, ct).ConfigureAwait(false);
            if (stamped is null
                || !string.Equals(stamped.ReportedMetricsPolicy, policy, StringComparison.Ordinal))
            {
                ledger.CacheStampsNotWritten++;
                return;
            }

            ledger.CacheStampsWritten++;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A cache failure must never abort a run or lose the ledger write that already happened; the
            // filing simply re-analyzes later.
            ledger.CacheStampsNotWritten++;
            _logger.LogWarning(
                ex,
                "Failed to stamp the analyzed-filing cache record for accession {Accession} with "
                    + "reported-metrics policy {Policy}; the filing will re-analyze on a later run.",
                accession,
                policy);
        }
    }

    /// <summary>
    /// SPEC 216 §2 — FAILS CLOSED on a cache record that carries a reported-metrics policy stamp with NO
    /// outbox envelope behind it. That is the 215-era shape: the extraction happened, the cache said
    /// "done", and the payload was lost before the ledger. It is counted and named once per accession, and
    /// it is deliberately NOT auto-re-analyzed — recovery is a conscious maintainer action. Measured
    /// 2026-09-08 on the live cache: 500 records, ZERO carrying any policy stamp at all.
    /// </summary>
    private async Task CheckReportedMetricsPolicyStampAsync(
        string policy, string accession, ReportedMetricLedgerTally ledger, CancellationToken ct)
    {
        if (_reportedMetricOutbox is null)
        {
            // Unreachable in the shipped composition (the outbox and the ledger are registered together),
            // and counted anyway: "nothing may be discarded without being counted" has no unreachable
            // exception, and an unreachable path that becomes reachable is exactly how a stamp with no
            // envelope behind it would go back to being invisible.
            ledger.PolicyStampNotChecked++;
            return;
        }

        if (await _reportedMetricOutbox.ExistsAsync(policy, accession, ct).ConfigureAwait(false))
        {
            return;
        }

        ledger.PolicyStampWithoutEnvelope++;
        _logger.LogWarning(
            "Analyzed-filing cache record for accession {Accession} claims reported-metrics policy {Policy} "
                + "but NO outbox envelope exists for it. The extraction was made and lost before it reached "
                + "the ledger (the pre-spec-216 shape). It is NOT treated as an empty ledger and it is NOT "
                + "auto-re-analyzed: recovery is a conscious maintainer re-analysis.",
            accession,
            policy);
    }

    /// <summary>How many failed ledger attempts on one envelope earn the per-accession Warning (spec 216 §2).</summary>
    private const int OutboxAttemptWarningThreshold = 3;

    /// <summary>The per-run reported-metrics ledger tally (spec 215 §1, widened by spec 216), rendered in ONE aggregated line.</summary>
    private sealed class ReportedMetricLedgerTally
    {
        public int Extractions;
        public int FilesWritten;
        public int FilesAlreadyOnDisk;
        public int FilesNotPersisted;
        public int NoResolvedCompany;
        public int NoStoreRegistered;
        public int RecordsWritten;
        public int DroppedUnverified;
        public int DroppedUnrecognised;
        public int DroppedDuplicate;
        public int PriorPairsDroppedIncomplete;

        // Spec 216 §3 — the new verifier drop classes, each on its own axis.
        public int DroppedMetricNotInQuote;
        public int DroppedPeriodNotInQuote;
        public int DroppedFragment;
        public int DroppedNotAssociated;

        // Spec 216 §2 — the outbox axes.
        public int OutboxPending;
        public int OutboxReplayed;
        public int OutboxAcknowledged;
        public int OutboxAttemptsExhausted;
        public int OutboxNotEnqueued;
        public int OutboxUnresolvedCompany;
        public int OutboxAttemptUpdatesNotPersisted;
        public int OutboxNotAcknowledged;
        public int CacheStampsWritten;
        public int CacheStampsNotWritten;
        public int PolicyStampWithoutEnvelope;
        public int PolicyStampNotChecked;

        // Spec 223 §2 — durable enqueues (OutboxPayloadsEnqueued): the successful EnqueueAsync outcomes,
        // fresh and re-enqueued-after-resolution alike.
        public int OutboxEnqueued;
    }

    /// <summary>
    /// SPEC 223 §2 — reads the ledger's ACCRUED inventory once per pass, AFTER this pass's fresh reads and
    /// outbox work so the number includes what this pass wrote. <c>null</c> when no ledger is registered
    /// (there is nothing to inventory); a store that throws is caught and rendered as not-recorded with
    /// the failure named — never as 0 and never a failure of the pass. Only cancellation propagates.
    /// </summary>
    private async Task<ReportedMetricLedgerInventory?> InventoryReportedMetricLedgerAsync(CancellationToken ct)
    {
        if (_reportedMetricStore is null)
        {
            return null;
        }

        try
        {
            return await _reportedMetricStore.InventoryAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Reported-metrics ledger inventory could not be read this pass; LedgerEntriesOnDisk renders as not recorded.");
            return ReportedMetricLedgerInventory.NotRecorded($"inventory read failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// SPEC 223 §2 — renders <c>LedgerEntriesOnDisk</c>. A registered ledger with an established count
    /// renders the measured total (an absent root directory is the measured zero and SAYS the directory
    /// is absent); anything the store could not establish renders as <c>not recorded (&lt;reason&gt;)</c>,
    /// never as <c>0</c>; an unregistered ledger renders <c>not recorded (ledger not registered)</c>.
    /// </summary>
    internal static string RenderLedgerInventory(ReportedMetricLedgerInventory? inventory, bool ledgerRegistered)
    {
        // The not-recorded branches are the record's own (shared with NewsJudgmentGenerator's per-cohort
        // line, so the two renderings cannot drift); only the richer MEASURED form is this line's. A null
        // inventory is unreachable when registered (the reader above always returns a record) and is
        // rendered honestly by the helper anyway rather than as a zero.
        if (ReportedMetricLedgerInventory.DescribeNotRecorded(inventory, ledgerRegistered) is { } notRecorded)
        {
            return notRecorded;
        }

        // DescribeNotRecorded returned null, so the ledger is registered, the inventory is present and both
        // counts are recorded.
        var (records, files) = inventory!.RecordedCounts;
        var unreadable = inventory.UnreadableFiles is { } u
            ? string.Create(CultureInfo.InvariantCulture, $"{u} unreadable")
            : "unreadable not recorded";
        var root = inventory.RootDirectoryExists switch
        {
            false => "ledger root directory absent",
            true => "ledger root directory present",
            null => "ledger root directory existence not recorded",
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{records} record(s) in {files} ledger file(s) ({unreadable}; {root})");
    }

    /// <summary>
    /// True when a directional filing read supersedes this extracted signal (spec 78): the extracted
    /// signal is a <see cref="SignalType.GuidanceChange"/> AND its filing evidence is in the supersede set
    /// (the distinct EvidenceIds of the produced directional GuidanceChange signals). The pass then skips
    /// storing this signal — by construction the only deterministic GuidanceChange an item-2.02 filing
    /// produces today is the spec-57 Neutral, so this suppresses exactly that one signal and nothing on
    /// non-filing evidence. Direction is intentionally NOT parsed: ANY GuidanceChange over a superseded
    /// filing is replaced by the better-informed directional read; keying on type + evidence is sufficient.
    /// The type compare is defensive against unknown/unparseable SignalType strings (a non-GuidanceChange
    /// string simply never matches).
    /// </summary>
    private static bool IsSupersededGuidanceChange(
        ExtractedSignal extracted,
        EvidenceItem evidence,
        HashSet<Guid> supersededFilingEvidenceIds) =>
        extracted.SignalType == nameof(SignalType.GuidanceChange)
        && supersededFilingEvidenceIds.Contains(evidence.Id);

    /// <summary>
    /// The result of <see cref="MapResolveReviewStoreAsync"/>: which run-summary counters the caller should
    /// bump. <see cref="Dropped"/> means the signal failed mapping/validation (no counters);
    /// <see cref="OtherValid"/> means it was stored valid but with a non-approved, non-review status.
    /// </summary>
    private enum SignalStoreOutcome
    {
        Dropped,
        Approved,
        NeedsReview,
        OtherValid,
    }

    /// <summary>
    /// What <see cref="MapResolveReviewStoreAsync"/> observed: which run-summary counter the caller should
    /// bump, and — on its own axis (spec 193 §1) — whether the durable mirror write FAILED. The two are
    /// independent: a signal can be Approved and not persisted, which is precisely the state that used to be
    /// invisible. A <see cref="SignalStoreOutcome.Dropped"/> signal never reaches the store, so it can never
    /// carry a durable-write failure.
    /// </summary>
    private readonly record struct SignalStoreResult(
        SignalStoreOutcome Outcome,
        bool NotPersisted,
        // Spec 215 §1: the company the stored signal RESOLVED to (null when unresolved or Dropped), so the
        // reported-metrics ledger files under the same company as the signal — resolution stays in one place.
        Guid? CompanyId,
        // Spec 216 §2: the mention text resolution was attempted FROM, so an outbox envelope whose company
        // could not be resolved carries everything a LATER pass needs to try again. Null for a Dropped
        // signal, which never reached the resolver.
        string? CompanyMention = null)
    {
        public static SignalStoreResult Of(
            SignalStoreOutcome outcome,
            bool notPersisted = false,
            Guid? companyId = null,
            string? companyMention = null) =>
            new(outcome, notPersisted, companyId, companyMention);
    }

    /// <summary>
    /// Pairs a newly-stored <see cref="EvidenceItem"/> with the collector-supplied company hints so the
    /// pass can pass them to the resolver without re-parsing the evidence's MetadataJson. The hints
    /// drive the resolver's high-confidence hint path.
    /// </summary>
    private readonly record struct CollectedEvidenceEntry(
        EvidenceItem Evidence, IReadOnlyList<string> CompanyHints);
}
