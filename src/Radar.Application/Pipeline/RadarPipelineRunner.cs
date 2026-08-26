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

        // Stage 7: optional report. The builder's exact per-strategy section instances (spec 179 §2) are
        // kept so the returned result can hand them to the in-process news-risk shadow step — the one
        // structured row source; never re-read, never re-ranked.
        Guid? reportId = null;
        IReadOnlyList<StrategyReportSection>? strategySections = null;
        if (_options.GenerateReport)
        {
            var report = await _reportBuilder
                .GenerateAsync(collection.AsOfUtc, collection.Collection, collection.Health, ct)
                .ConfigureAwait(false);
            await _reportFileWriter.WriteAsync(report.Report, ct).ConfigureAwait(false);
            reportId = report.Report.Id;
            strategySections = report.StrategySections;
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

        // Spec 193 §1: a run that lost a durable write must SAY SO in its summary. Appended as a SEPARATE
        // statement on the non-zero path only, deliberately: the existing line above must stay byte-identical
        // for the healthy run (a pinned criterion), so the counts are not folded into its template.
        LogDurableWriteShortfall(
            _logger, collection.SignalsNotPersisted, scoring.ScoreSnapshotsNotPersisted);

        // The run id is minted ONCE (spec 179 §2): the SAME value is written to the durable run record
        // below and returned on the pipeline result, so the shadow step's persisted assessments reference
        // exactly the run record that exists on disk — never a second id for the same run.
        var runId = Guid.NewGuid();

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
            Collection: collection.Collection,
            RunId: runId,
            StrategySections: strategySections);

        // Persist a durable run record (append-only run log, AD-8). Best-effort like the other file
        // stores: the store swallows disk errors, so a failure here never changes a counter or aborts
        // the run. Reuse asOfUtc (AD-7: one run, one instant) and the collection pass's already-ordered
        // collector names so the record reflects what actually ran. The result is returned only AFTER
        // this write is awaited (spec 179 §2: the write is attempted before anything downstream
        // consumes RunId) — but because the store degrades on disk failure, durability is attempted,
        // not guaranteed, and consumers of RunId must tolerate a missing/unreadable run record.
        var runRecord = new PipelineRunRecord(
            Id: runId,
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
            PrimaryStrategy: scoring.PrimaryStrategy,
            // This run collected the whole watch universe (a filter is collect-only by guard, spec 161).
            CompanyFilter: null,
            // Per-collector run provenance (spec 169), captured by the collection pass before the merge
            // discarded collector identity. Observational only — see PipelineRunRecord.CollectorRuns.
            CollectorRuns: collection.CollectorRuns,
            // The spec-177 news-observation batch this pass wrote — the explicit manifest↔run association.
            NewsObservationBatchId: collection.NewsObservationBatchId,
            // Spec 193 §1: the combined run did BOTH kinds of work, so it genuinely observed both counts —
            // 0 here is a measured zero, not a fabricated one.
            SignalsNotPersisted: collection.SignalsNotPersisted,
            ScoreSnapshotsNotPersisted: scoring.ScoreSnapshotsNotPersisted);
        await _runStore.WriteAsync(runRecord, ct).ConfigureAwait(false);

        return pipelineResult;
    }

    /// <summary>
    /// The ONE rendering of spec 193 §1's summary-line shortfall statement, shared by every runner that can
    /// observe one, so the three cannot drift into three different wordings of the same fact. Emitted only
    /// when something really was lost: a healthy run's log is byte-identical to pre-193 output, which is a
    /// pinned criterion, and a "0 not persisted" line on every run would be noise that trains the reader to
    /// skip it. Null means the pass did not do that kind of work and is never rendered as a zero.
    /// </summary>
    internal static void LogDurableWriteShortfall(
        ILogger logger, int? signalsNotPersisted, int? scoreSnapshotsNotPersisted)
    {
        if (signalsNotPersisted is not > 0 && scoreSnapshotsNotPersisted is not > 0)
        {
            return;
        }

        logger.LogWarning(
            "This run did NOT durably persist everything it produced: {SignalsNotPersisted} signal(s) and "
                + "{ScoreSnapshotsNotPersisted} score snapshot(s) exist only in this process's memory. The "
                + "run completed and reported on them, but they are absent from the accrued stores, so the "
                + "next run's history read and the efficacy/replay reads will not see them.",
            signalsNotPersisted ?? 0,
            scoreSnapshotsNotPersisted ?? 0);
    }
}
