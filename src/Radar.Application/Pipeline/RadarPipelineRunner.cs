using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Pipeline;

/// <summary>
/// Provider-independent deterministic orchestration of the seven pipeline stages. Sequences the
/// existing Application interfaces (collect → store evidence → extract → resolve → review → store
/// signals → score → report) and threads provenance through them. Contains <b>no</b> scoring math,
/// <b>no</b> label thresholds, and <b>no</b> resolution/extraction logic — each stage's behaviour
/// stays behind its own interface; the runner only sequences them.
/// </summary>
public sealed class RadarPipelineRunner : IRadarPipeline
{
    private readonly IEvidenceCollector _collector;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly ISignalExtractor _extractor;
    private readonly ICompanyResolver _resolver;
    private readonly ISignalReviewer _reviewer;
    private readonly ISignalRepository _signalRepository;
    private readonly ICompanyRepository _companyRepository;
    private readonly IScoringEngine _scoringEngine;
    private readonly IWeeklyReportBuilder _reportBuilder;
    private readonly PipelineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RadarPipelineRunner> _logger;

    public RadarPipelineRunner(
        IEvidenceCollector collector,
        IEvidenceRepository evidenceRepository,
        ISignalExtractor extractor,
        ICompanyResolver resolver,
        ISignalReviewer reviewer,
        ISignalRepository signalRepository,
        ICompanyRepository companyRepository,
        IScoringEngine scoringEngine,
        IWeeklyReportBuilder reportBuilder,
        PipelineOptions options,
        TimeProvider timeProvider,
        ILogger<RadarPipelineRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(evidenceRepository);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(signalRepository);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(scoringEngine);
        ArgumentNullException.ThrowIfNull(reportBuilder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _collector = collector;
        _evidenceRepository = evidenceRepository;
        _extractor = extractor;
        _resolver = resolver;
        _reviewer = reviewer;
        _signalRepository = signalRepository;
        _companyRepository = companyRepository;
        _scoringEngine = scoringEngine;
        _reportBuilder = reportBuilder;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RadarPipelineResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // A single run instant feeds the mapper's createdAtUtc, the scoring windowEndUtc, and the
        // report periodEndUtc so the whole run is internally consistent. TimeProvider.GetUtcNow()
        // already returns a zero-offset DateTimeOffset (the report builder requires zero offset).
        var asOfUtc = _timeProvider.GetUtcNow();

        var evidenceCollected = 0;
        var evidenceNew = 0;
        var signalsExtracted = 0;
        var signalsValid = 0;
        var signalsApproved = 0;
        var signalsNeedingReview = 0;
        var companiesScored = 0;

        // Stage 1 + 2: collect (text already normalized + content-hashed by the collector), then
        // dedupe-store. Only newly-stored evidence is extracted so re-collected duplicates never
        // produce duplicate signals. Iterate in the collector's returned (deterministic) order.
        var collected = await _collector.CollectAsync(ct).ConfigureAwait(false);
        var newEvidence = new List<EvidenceItem>();
        foreach (var item in collected)
        {
            evidenceCollected++;
            if (await _evidenceRepository.AddIfNewAsync(item, ct).ConfigureAwait(false))
            {
                evidenceNew++;
                newEvidence.Add(item);
            }
        }

        // Stage 4 + 3 + 5: extract → resolve → review → store, per new evidence, in order.
        foreach (var evidence in newEvidence)
        {
            var output = await _extractor.ExtractAsync(evidence, ct).ConfigureAwait(false);
            foreach (var extracted in output.Signals)
            {
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
                var resolution = await _resolver.ResolveAsync(signal.CompanyMention, ct).ConfigureAwait(false);
                if (resolution.CompanyId is { } companyId)
                {
                    signal = signal with { CompanyId = companyId };
                }

                // Review may only lower confidence and set the review status.
                var outcome = await _reviewer.ReviewAsync(signal, evidence, ct).ConfigureAwait(false);

                // Store the reviewed signal. Known MVP gap: the reviewer also returns an audit
                // SignalReview record (outcome.Review), but there is currently no
                // ISignalReviewRepository, so that audit record is NOT persisted in this slice —
                // only the reviewed signal's ReviewStatus/Confidence are. Persisting the audit
                // trail is a future slice, not this one.
                await _signalRepository.AddAsync(outcome.ReviewedSignal, ct).ConfigureAwait(false);

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
        // have signals (a company with no in-window signals yields a valid neutral snapshot).
        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);
        foreach (var company in companies)
        {
            await _scoringEngine.ScoreCompanyAsync(company.Id, asOfUtc, ct).ConfigureAwait(false);
            companiesScored++;
        }

        // Stage 7: optional report.
        Guid? reportId = null;
        if (_options.GenerateReport)
        {
            var report = await _reportBuilder.GenerateAsync(asOfUtc, ct).ConfigureAwait(false);
            reportId = report.Report.Id;
        }

        _logger.LogInformation(
            "Pipeline run complete: {EvidenceNew}/{EvidenceCollected} new evidence, " +
            "{SignalsApproved} approved / {SignalsNeedingReview} needs-review signals, " +
            "{CompaniesScored} companies scored, report {ReportId}.",
            evidenceNew,
            evidenceCollected,
            signalsApproved,
            signalsNeedingReview,
            companiesScored,
            reportId?.ToString() ?? "none");

        return new RadarPipelineResult(
            EvidenceCollected: evidenceCollected,
            EvidenceNew: evidenceNew,
            SignalsExtracted: signalsExtracted,
            SignalsValid: signalsValid,
            SignalsApproved: signalsApproved,
            SignalsNeedingReview: signalsNeedingReview,
            CompaniesScored: companiesScored,
            ReportId: reportId);
    }
}
