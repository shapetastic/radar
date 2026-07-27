using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Filings;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
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

    // OPT-IN directional filing enrichment (AI only). Null when AI is disabled (the shipped default), in
    // which case the enrichment step is skipped entirely and the default pipeline is byte-for-byte
    // unchanged. .NET DI supplies the null default when the service is not registered.
    private readonly IDirectionalFilingSignalSource? _directionalFilingSignals;

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
        IDirectionalFilingSignalSource? directionalFilingSignals = null)
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
        _directionalFilingSignals = directionalFilingSignals;
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
        var results = new List<CollectionResult>(_collectors.Count);
        foreach (var collector in _collectors)
        {
            ct.ThrowIfCancellationRequested();
            var result = await collector.CollectAsync(context, ct).ConfigureAwait(false);
            results.Add(result with
            {
                Evidence = [.. result.Evidence.Select(
                    e => CollectionProvenanceMetadata.Stamp(e, collector.CollectorName))],
            });
        }

        var collected = CollectionResultMerger.Merge(results);
        var newEvidence = new List<CollectedEvidenceEntry>();
        foreach (var item in collected.Evidence)
        {
            ct.ThrowIfCancellationRequested();

            evidenceCollected++;
            var evidence = _mapper.ToEvidenceItem(item);
            if (await _evidenceRepository.AddIfNewAsync(evidence, ct).ConfigureAwait(false))
            {
                evidenceNew++;
                newEvidence.Add(new CollectedEvidenceEntry(evidence, item.CompanyHints));

                // Mirror each newly-stored item to the insert-only on-disk raw store (AD-8). The file
                // store is the on-disk twin of the immutable repository; a false return is just a
                // dedupe/disk skip and must not abort the run or change any counters.
                await _rawEvidenceStore.WriteIfNewAsync(evidence, ct).ConfigureAwait(false);
            }
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

                switch (await MapResolveReviewStoreAsync(extracted, evidence, entry.CompanyHints, asOfUtc, ct)
                    .ConfigureAwait(false))
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
        foreach (var d in directional)
        {
            ct.ThrowIfCancellationRequested();

            signalsExtracted++;

            // Resolve with the filing evidence's own collector hints when present; an absent entry
            // (defensive — every produced signal's evidence came from candidates) falls back to the
            // empty list, i.e. the CompanyMention (= filing SourceName) path.
            var directionalHints = hintsByEvidenceId.GetValueOrDefault(d.Evidence.Id, []);
            switch (await MapResolveReviewStoreAsync(d.Signal, d.Evidence, directionalHints, asOfUtc, ct)
                .ConfigureAwait(false))
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
            Companies: companies);
    }

    /// <summary>
    /// The shared map -&gt; resolve -&gt; review -&gt; store -&gt; file tail used by BOTH the deterministic
    /// keyword extract loop and the opt-in directional filing enrichment. The mapper owns the provenance
    /// check (excerpt must be found in the evidence) and validation — the pass does not re-validate.
    /// Returns which counters the caller should bump (kept in the caller so the run-summary locals stay in
    /// one place).
    /// </summary>
    private async Task<SignalStoreOutcome> MapResolveReviewStoreAsync(
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
            return SignalStoreOutcome.Dropped;
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
        // overwrites last-write-wins), and the store swallows disk errors, so this must not
        // change any counter or abort the run.
        await _signalFileStore
            .WriteAsync(outcome.ReviewedSignal, outcome.Review, ct).ConfigureAwait(false);

        return outcome.ReviewedSignal.ReviewStatus switch
        {
            SignalReviewStatus.Approved => SignalStoreOutcome.Approved,
            SignalReviewStatus.NeedsHumanReview or SignalReviewStatus.Pending => SignalStoreOutcome.NeedsReview,
            _ => SignalStoreOutcome.OtherValid,
        };
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
    /// Pairs a newly-stored <see cref="EvidenceItem"/> with the collector-supplied company hints so the
    /// pass can pass them to the resolver without re-parsing the evidence's MetadataJson. The hints
    /// drive the resolver's high-confidence hint path.
    /// </summary>
    private readonly record struct CollectedEvidenceEntry(
        EvidenceItem Evidence, IReadOnlyList<string> CompanyHints);
}
