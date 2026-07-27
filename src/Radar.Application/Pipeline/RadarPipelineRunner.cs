using Microsoft.Extensions.Logging;

using Radar.Application.Reporting;
using Radar.Application.Scoring;

namespace Radar.Application.Pipeline;

/// <summary>
/// Provider-independent deterministic orchestration of the seven pipeline stages. Sequences the
/// existing Application interfaces (collect → store evidence → extract → resolve → review → store
/// signals → score → report) and threads provenance through them. Contains <b>no</b> scoring math,
/// <b>no</b> label thresholds, and <b>no</b> resolution/extraction logic — each stage's behaviour stays
/// behind its own interface; the runner only sequences them.
/// <para>
/// Spec 144: stages 1–5 live in <see cref="ICollectionPass"/> and stage 6 in <see cref="IScoringPass"/>, so
/// the SAME code runs whether the operator invokes the combined pass, a standalone <c>collect</c>
/// (<see cref="CollectOnlyPipelineRunner"/>) or a standalone <c>score</c>
/// (<see cref="ScoreOnlyPipelineRunner"/>). This runner is the COMBINED pass and its observable behaviour —
/// stage order, counters, log line and run record — is unchanged by that split.
/// </para>
/// <para>
/// Spec 137: the scoring stage is the ONLY plural stage. It iterates
/// <see cref="IScoringStrategyFactory.Runtimes"/> × companies, so one collection pass feeds N
/// independently-stamped scorings; everything above it — collection, the AI directional read, extraction,
/// resolution, review and signal persistence — is shared and runs exactly once.
/// </para>
/// <para>
/// Spec 141: the run opens with the <see cref="StrategyIdentityGuard"/> tripwire (before Stage 1), so a
/// strategy edited in place — which the strategy-name series key forbids — fails the run before any
/// collection work happens rather than after it has silently extended the wrong series.
/// </para>
/// </summary>
public sealed class RadarPipelineRunner : IRadarPipeline
{
    private readonly ICollectionPass _collectionPass;
    private readonly IScoringPass _scoringPass;
    private readonly IScoringStrategyFactory _scoringStrategies;
    private readonly IScoringConfigStore _scoringConfigStore;
    private readonly IWeeklyReportBuilder _reportBuilder;
    private readonly IReportFileWriter _reportFileWriter;
    private readonly IPipelineRunStore _runStore;
    private readonly PipelineOptions _options;
    private readonly ILogger<RadarPipelineRunner> _logger;

    public RadarPipelineRunner(
        ICollectionPass collectionPass,
        IScoringPass scoringPass,
        IScoringStrategyFactory scoringStrategies,
        IScoringConfigStore scoringConfigStore,
        IWeeklyReportBuilder reportBuilder,
        IReportFileWriter reportFileWriter,
        IPipelineRunStore runStore,
        PipelineOptions options,
        ILogger<RadarPipelineRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(collectionPass);
        ArgumentNullException.ThrowIfNull(scoringPass);
        ArgumentNullException.ThrowIfNull(scoringStrategies);
        ArgumentNullException.ThrowIfNull(scoringConfigStore);
        ArgumentNullException.ThrowIfNull(reportBuilder);
        ArgumentNullException.ThrowIfNull(reportFileWriter);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _collectionPass = collectionPass;
        _scoringPass = scoringPass;
        _scoringStrategies = scoringStrategies;
        _scoringConfigStore = scoringConfigStore;
        _reportBuilder = reportBuilder;
        _reportFileWriter = reportFileWriter;
        _runStore = runStore;
        _options = options;
        _logger = logger;
    }

    public async Task<RadarPipelineResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Stage 0 (spec 141): strategy-identity tripwire, BEFORE any collection work so a misconfiguration
        // costs no network calls and no partial run. Each configured strategy's computed fingerprint is
        // checked against the one recorded for its NAME; a name whose fingerprint moved was edited in place,
        // which the immutability convention behind the strategy-name series key forbids, and the run fails
        // fast naming the strategy. A collector toggle cannot trip this — the collector set is no longer a
        // fingerprint input, only recorded provenance on each snapshot.
        await StrategyIdentityGuard
            .VerifyAsync(_scoringStrategies.Runtimes, _scoringConfigStore, _logger, ct)
            .ConfigureAwait(false);

        // Stages 1–5: collect → store evidence → extract → resolve → review → store signals. Runs exactly
        // once (spec 137) and owns the run's asOfUtc capture, which must happen AFTER collection.
        var collection = await _collectionPass.RunAsync(ct).ConfigureAwait(false);

        // Stage 6: score every company at the run instant, once per configured strategy. Reuses the company
        // list the collection pass already loaded, so the run still makes a single company-repository read.
        var scoring = await _scoringPass
            .RunAsync(collection.Companies, collection.AsOfUtc, ct)
            .ConfigureAwait(false);

        // Stage 7: optional report.
        Guid? reportId = null;
        if (_options.GenerateReport)
        {
            var report = await _reportBuilder
                .GenerateAsync(collection.AsOfUtc, collection.Collection, collection.Health, ct)
                .ConfigureAwait(false);
            await _reportFileWriter.WriteAsync(report.Report, ct).ConfigureAwait(false);
            reportId = report.Report.Id;
        }

        _logger.LogInformation(
            "Pipeline run complete: {EvidenceNew}/{EvidenceCollected} new evidence, " +
            "{SignalsApproved} approved / {SignalsNeedingReview} needs-review signals, " +
            "{CompaniesScored} companies scored by the primary of {StrategyCount} strategies, " +
            "{SourcesFailed}/{SourcesChecked} sources unreadable, report {ReportId}.",
            collection.EvidenceNew,
            collection.EvidenceCollected,
            collection.SignalsApproved,
            collection.SignalsNeedingReview,
            scoring.CompaniesScored,
            scoring.Strategies.Count,
            collection.Collection.SourcesFailed,
            collection.Collection.SourcesChecked,
            reportId?.ToString() ?? "none");

        var pipelineResult = new RadarPipelineResult(
            EvidenceCollected: collection.EvidenceCollected,
            EvidenceNew: collection.EvidenceNew,
            SignalsExtracted: collection.SignalsExtracted,
            SignalsValid: collection.SignalsValid,
            SignalsApproved: collection.SignalsApproved,
            SignalsNeedingReview: collection.SignalsNeedingReview,
            CompaniesScored: scoring.CompaniesScored,
            ReportId: reportId,
            SourcesChecked: collection.Collection.SourcesChecked,
            SourcesFailed: collection.Collection.SourcesFailed,
            Collection: collection.Collection);

        // Persist a durable run record (append-only run log, AD-8). Best-effort like the other file
        // stores: the store swallows disk errors, so a failure here never changes a counter or aborts
        // the run. Reuse asOfUtc (AD-7: one run, one instant) and the collection pass's already-ordered
        // collector names so the record reflects what actually ran.
        var runRecord = new PipelineRunRecord(
            Id: Guid.NewGuid(),
            CreatedAtUtc: collection.AsOfUtc,
            Collectors: collection.Collectors,
            EvidenceCollected: pipelineResult.EvidenceCollected,
            EvidenceNew: pipelineResult.EvidenceNew,
            SignalsExtracted: pipelineResult.SignalsExtracted,
            SignalsValid: pipelineResult.SignalsValid,
            SignalsApproved: pipelineResult.SignalsApproved,
            SignalsNeedingReview: pipelineResult.SignalsNeedingReview,
            CompaniesScored: pipelineResult.CompaniesScored,
            SourcesChecked: pipelineResult.SourcesChecked,
            SourcesFailed: pipelineResult.SourcesFailed,
            ReportId: pipelineResult.ReportId,
            CollectionWarnings: collection.Health.Warnings,
            // The scoring strategies that ran, in run order, with the primary marked (spec 137) — the run
            // log's answer to "which scorings does this collection pass back?". Observational only.
            Strategies: scoring.Strategies,
            PrimaryStrategy: scoring.PrimaryStrategy);
        await _runStore.WriteAsync(runRecord, ct).ConfigureAwait(false);

        return pipelineResult;
    }
}
