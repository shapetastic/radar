using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Pipeline;

/// <summary>
/// Provider-independent deterministic orchestration of the seven pipeline stages. Sequences the
/// existing Application interfaces (collect → store evidence → extract → resolve → review → store
/// signals → score → report) and threads provenance through them. The collect stage runs <b>all</b>
/// registered collectors in a stable <see cref="IEvidenceCollector.CollectorName"/> order and merges
/// their results (via <see cref="CollectionResultMerger"/>) before storing evidence. Contains
/// <b>no</b> scoring math, <b>no</b> label thresholds, and <b>no</b> resolution/extraction logic —
/// each stage's behaviour stays behind its own interface; the runner only sequences them.
/// </summary>
public sealed class RadarPipelineRunner : IRadarPipeline
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
    private readonly IScoringEngine _scoringEngine;
    private readonly IScoreSnapshotFileStore _scoreFileStore;
    private readonly IWeeklyReportBuilder _reportBuilder;
    private readonly IReportFileWriter _reportFileWriter;
    private readonly IPipelineRunStore _runStore;
    private readonly PipelineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RadarPipelineRunner> _logger;

    public RadarPipelineRunner(
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
        IScoringEngine scoringEngine,
        IScoreSnapshotFileStore scoreFileStore,
        IWeeklyReportBuilder reportBuilder,
        IReportFileWriter reportFileWriter,
        IPipelineRunStore runStore,
        PipelineOptions options,
        TimeProvider timeProvider,
        ILogger<RadarPipelineRunner> logger)
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
        ArgumentNullException.ThrowIfNull(scoringEngine);
        ArgumentNullException.ThrowIfNull(scoreFileStore);
        ArgumentNullException.ThrowIfNull(reportBuilder);
        ArgumentNullException.ThrowIfNull(reportFileWriter);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(options);
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
        _scoringEngine = scoringEngine;
        _scoreFileStore = scoreFileStore;
        _reportBuilder = reportBuilder;
        _reportFileWriter = reportFileWriter;
        _runStore = runStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RadarPipelineResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var evidenceCollected = 0;
        var evidenceNew = 0;
        var signalsExtracted = 0;
        var signalsValid = 0;
        var signalsApproved = 0;
        var signalsNeedingReview = 0;
        var companiesScored = 0;

        // Stage 1 + 2: collect raw evidence over the watch universe, map each result to an immutable
        // domain EvidenceItem (normalization, hashing, quality parsing live in the mapper), then
        // dedupe-store. Only newly-stored evidence is extracted so re-collected duplicates never
        // produce duplicate signals. Iterate in the collector's returned (deterministic) order. The
        // companies are loaded once up front: the collection context needs them and Stage 6 reuses
        // the same list for scoring.
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
        var sourceFeeds = await _companyRepository.GetSourceFeedsAsync(ct).ConfigureAwait(false);
        var context = new CollectionContext(companies, sourceFeeds);

        // Run every registered collector sequentially in the stable order fixed in the constructor
        // (keeps determinism and avoids hammering the network), then merge their results into one. The
        // merge concatenates evidence in collector order without re-sorting/de-duping; cross-collector
        // duplicates resolve downstream via the insert-only ContentHash dedupe (AD-1).
        var results = new List<CollectionResult>(_collectors.Count);
        foreach (var collector in _collectors)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await collector.CollectAsync(context, ct).ConfigureAwait(false));
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

        // Stage 4 + 3 + 5: extract → resolve → review → store, per new evidence, in order. Each
        // evidence's collector hints (entry.CompanyHints) are passed to the resolver so a
        // company-specific feed's binding can drive a high-confidence resolution.
        foreach (var entry in newEvidence)
        {
            ct.ThrowIfCancellationRequested();

            var evidence = entry.Evidence;
            var output = await _extractor.ExtractAsync(evidence, ct).ConfigureAwait(false);
            foreach (var extracted in output.Signals)
            {
                ct.ThrowIfCancellationRequested();

                signalsExtracted++;

                // The mapper owns the provenance check (excerpt must be found in the evidence) and
                // validation — the runner does not re-validate.
                var mapping = ExtractedSignalMapper.ToSignal(extracted, evidence, asOfUtc);
                if (!mapping.IsValid)
                {
                    _logger.LogDebug(
                        "Dropping invalid extracted signal for evidence {EvidenceId}: {Errors}",
                        evidence.Id,
                        string.Join("; ", mapping.Errors));
                    continue;
                }

                var signal = mapping.Signal!;
                signalsValid++;

                // Resolve: only ADD a CompanyId when matched; never guess. An unresolved mention
                // stays CompanyId == null and the reviewer routes it to human review.
                var resolution = await _resolver
                    .ResolveAsync(signal.CompanyMention, entry.CompanyHints, ct).ConfigureAwait(false);
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

                switch (outcome.ReviewedSignal.ReviewStatus)
                {
                    case SignalReviewStatus.Approved:
                        signalsApproved++;
                        break;
                    case SignalReviewStatus.NeedsHumanReview:
                    case SignalReviewStatus.Pending:
                        signalsNeedingReview++;
                        break;
                }
            }
        }

        // Stage 6: score every company at asOfUtc. The engine applies the window/Approved-only
        // filter and writes the snapshot + links; the runner does not pre-filter which companies
        // have signals (a company with no in-window signals yields a valid neutral snapshot). Reuses
        // the company list loaded up front for the collection context (single repository read).
        // The score file store mirrors each snapshot + its links to disk (AD-8), the durable twin of
        // the in-memory score repository. Snapshots are upsert-by-Id (the store overwrites
        // last-write-wins), and the store swallows disk errors, so this must not change any counter or
        // abort the run. Every scored company still increments companiesScored, including neutral
        // zero-signal snapshots.
        foreach (var company in companies)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _scoringEngine.ScoreCompanyAsync(company.Id, asOfUtc, ct).ConfigureAwait(false);
            await _scoreFileStore.WriteAsync(result.Snapshot, result.Links, ct).ConfigureAwait(false);
            companiesScored++;
        }

        // Stage 7: optional report.
        Guid? reportId = null;
        if (_options.GenerateReport)
        {
            var report = await _reportBuilder
                .GenerateAsync(asOfUtc, collected.Summary, ct)
                .ConfigureAwait(false);
            await _reportFileWriter.WriteAsync(report.Report, ct).ConfigureAwait(false);
            reportId = report.Report.Id;
        }

        _logger.LogInformation(
            "Pipeline run complete: {EvidenceNew}/{EvidenceCollected} new evidence, " +
            "{SignalsApproved} approved / {SignalsNeedingReview} needs-review signals, " +
            "{CompaniesScored} companies scored, {SourcesFailed}/{SourcesChecked} sources unreadable, " +
            "report {ReportId}.",
            evidenceNew,
            evidenceCollected,
            signalsApproved,
            signalsNeedingReview,
            companiesScored,
            collected.Summary.SourcesFailed,
            collected.Summary.SourcesChecked,
            reportId?.ToString() ?? "none");

        var pipelineResult = new RadarPipelineResult(
            EvidenceCollected: evidenceCollected,
            EvidenceNew: evidenceNew,
            SignalsExtracted: signalsExtracted,
            SignalsValid: signalsValid,
            SignalsApproved: signalsApproved,
            SignalsNeedingReview: signalsNeedingReview,
            CompaniesScored: companiesScored,
            ReportId: reportId,
            SourcesChecked: collected.Summary.SourcesChecked,
            SourcesFailed: collected.Summary.SourcesFailed,
            Collection: collected.Summary);

        // Persist a durable run record (append-only run log, AD-8). Best-effort like the other file
        // stores: the store swallows disk errors, so a failure here never changes a counter or aborts
        // the run. Reuse asOfUtc (AD-7: one run, one instant) and the runner's already-ordered
        // collector names so the record reflects what actually ran.
        var runRecord = new PipelineRunRecord(
            Id: Guid.NewGuid(),
            CreatedAtUtc: asOfUtc,
            Collectors: _collectors.Select(c => c.CollectorName).ToArray(),
            EvidenceCollected: pipelineResult.EvidenceCollected,
            EvidenceNew: pipelineResult.EvidenceNew,
            SignalsExtracted: pipelineResult.SignalsExtracted,
            SignalsValid: pipelineResult.SignalsValid,
            SignalsApproved: pipelineResult.SignalsApproved,
            SignalsNeedingReview: pipelineResult.SignalsNeedingReview,
            CompaniesScored: pipelineResult.CompaniesScored,
            SourcesChecked: pipelineResult.SourcesChecked,
            SourcesFailed: pipelineResult.SourcesFailed,
            ReportId: pipelineResult.ReportId);
        await _runStore.WriteAsync(runRecord, ct).ConfigureAwait(false);

        return pipelineResult;
    }

    /// <summary>
    /// Pairs a newly-stored <see cref="EvidenceItem"/> with the collector-supplied company hints so the
    /// runner can pass them to the resolver without re-parsing the evidence's MetadataJson. The hints
    /// drive the resolver's high-confidence hint path.
    /// </summary>
    private readonly record struct CollectedEvidenceEntry(
        EvidenceItem Evidence, IReadOnlyList<string> CompanyHints);
}
