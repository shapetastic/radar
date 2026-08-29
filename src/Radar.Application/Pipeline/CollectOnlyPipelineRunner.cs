using Microsoft.Extensions.Logging;

using Radar.Application.EntityResolution;
using Radar.Application.Scoring;

namespace Radar.Application.Pipeline;

/// <summary>
/// The standalone <c>collect</c> pass (spec 144): stages 1–5 and nothing else. It writes the durable evidence
/// and signal stores, then the append-only run record — it does <b>not</b> score and does <b>not</b> report.
/// <para>
/// It is the same <see cref="ICollectionPass"/> the combined <see cref="RadarPipelineRunner"/> runs, so
/// "collect on its own schedule" costs no second copy of stages 1–5 and cannot drift from the combined run.
/// The scoring stage is simply absent: a later <see cref="ScoreOnlyPipelineRunner"/> pass scores whatever has
/// accrued, as often as the operator likes, with no re-collection — no collector runs there, so no SEC /
/// GDELT / Google-News traffic and no AI spend. That is narrower than "no requests at all"; see
/// <see cref="ScoreOnlyPipelineRunner"/> for the price-acquisition caveat.
/// </para>
/// <para>
/// The <see cref="StrategyIdentityGuard"/> tripwire still runs FIRST, for exactly the spec-141 reason: a
/// strategy edited in place must cost no collection. A collect pass does not score, but the run it feeds will,
/// and failing at the start of the collect pass is the cheapest place to find out.
/// </para>
/// <para>
/// The optional <see cref="EntityResolution.CompanyFilter"/> (spec 161, <c>Radar:Companies</c>) is PROVENANCE
/// here, not behaviour: the filter is applied at the seed source, so this runner only records which companies
/// the pass was restricted to — on the run record and in its summary log — so a partial pass is never
/// mistakable for a full one. It is <c>null</c> (unregistered) for every unfiltered run.
/// </para>
/// </summary>
public sealed class CollectOnlyPipelineRunner : IRadarPipeline
{
    private readonly ICollectionPass _collectionPass;
    private readonly IScoringStrategyFactory _scoringStrategies;
    private readonly IScoringConfigStore _scoringConfigStore;
    private readonly IPipelineRunStore _runStore;
    private readonly ILogger<CollectOnlyPipelineRunner> _logger;
    private readonly CompanyFilter? _companyFilter;

    public CollectOnlyPipelineRunner(
        ICollectionPass collectionPass,
        IScoringStrategyFactory scoringStrategies,
        IScoringConfigStore scoringConfigStore,
        IPipelineRunStore runStore,
        ILogger<CollectOnlyPipelineRunner> logger,
        CompanyFilter? companyFilter = null)
    {
        ArgumentNullException.ThrowIfNull(collectionPass);
        ArgumentNullException.ThrowIfNull(scoringStrategies);
        ArgumentNullException.ThrowIfNull(scoringConfigStore);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(logger);

        _collectionPass = collectionPass;
        _scoringStrategies = scoringStrategies;
        _scoringConfigStore = scoringConfigStore;
        _runStore = runStore;
        _logger = logger;
        _companyFilter = companyFilter;
    }

    public async Task<RadarPipelineResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Stage 0 (spec 141), kept first for the same reason the combined run keeps it first: a
        // misconfiguration costs no collection.
        await StrategyIdentityGuard
            .VerifyAsync(_scoringStrategies.Runtimes, _scoringConfigStore, _logger, ct)
            .ConfigureAwait(false);

        var collection = await _collectionPass.RunAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Collect-only run complete: {EvidenceNew}/{EvidenceCollected} new evidence, " +
            "{SignalsApproved} approved / {SignalsNeedingReview} needs-review signals, " +
            "{SourcesFailed}/{SourcesChecked} sources unreadable. No company was scored and no report was " +
            "generated — run a score pass over the accrued store.",
            collection.EvidenceNew,
            collection.EvidenceCollected,
            collection.SignalsApproved,
            collection.SignalsNeedingReview,
            collection.Collection.SourcesFailed,
            collection.Collection.SourcesChecked);

        // Spec 193 §1: say so when a durable write was lost. Separate statement on the non-zero path only, so
        // the line above stays byte-identical for a healthy run. This pass wrote no score snapshot, so it
        // passes null for that axis rather than a 0 it did not observe.
        RadarPipelineRunner.LogDurableWriteShortfall(
            _logger, collection.SignalsNotPersisted, scoreSnapshotsNotPersisted: null);

        // Spec 161: state the filter in the collection summary, so a partial pass reads as a partial pass in
        // the log as well as in the run record. Only the RETAINED companies are known here (the seed's total
        // is stated by the seed-source decorator's own line, above this one in the same run).
        if (_companyFilter is not null)
        {
            _logger.LogInformation(
                "This was a FILTERED collect pass: companies={Companies} — evidence was gathered for these "
                    + "{CompanyCount} named companies only, NOT the full watch universe. Scoring stays "
                    + "whole-universe on the next full/score run.",
                _companyFilter.Describe(),
                _companyFilter.Tickers.Count);
        }

        var pipelineResult = new RadarPipelineResult(
            EvidenceCollected: collection.EvidenceCollected,
            EvidenceNew: collection.EvidenceNew,
            SignalsExtracted: collection.SignalsExtracted,
            SignalsValid: collection.SignalsValid,
            SignalsApproved: collection.SignalsApproved,
            SignalsNeedingReview: collection.SignalsNeedingReview,
            // Nothing was scored and nothing was reported — reported as 0/null rather than omitted, so a
            // reader of the run log can tell a collect pass from a combined run that scored nothing.
            CompaniesScored: 0,
            ReportId: null,
            SourcesChecked: collection.Collection.SourcesChecked,
            SourcesFailed: collection.Collection.SourcesFailed,
            Collection: collection.Collection);

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
            // No strategy scored this pass. Left null (the "unrecorded" value old run JSON also carries)
            // rather than listing the configured strategies, which would claim a scoring that never happened.
            Strategies: null,
            PrimaryStrategy: null,
            // Spec 161: null for an unfiltered run (every existing record reads that way too), the canonical
            // ticker list when Radar:Companies restricted this pass. Provenance only.
            CompanyFilter: _companyFilter?.Tickers,
            // Per-collector run provenance (spec 169), captured by the SAME collection pass the combined run
            // uses. A FILTERED pass still records it truthfully — the evaluator rejects the checkpoint on
            // CompanyFilter, not on missing coverage, so a partial pass can never prove primary-screen
            // coverage even though its rows are honest about what it did look at.
            CollectorRuns: collection.CollectorRuns,
            // The spec-177 news-observation batch this pass wrote — the explicit manifest↔run association.
            // A FILTERED pass may capture observations; its batch records FullUniverse=false, so it can
            // never establish the whole-universe prospective boundary.
            NewsObservationBatchId: collection.NewsObservationBatchId,
            // Spec 193 §1: this pass DID write signals, so its count is a measured fact. It scored nothing,
            // so ScoreSnapshotsNotPersisted stays null ("this pass did not do that work") — a 0 would claim a
            // clean snapshot write that never happened, the same reason Strategies/CollectorRuns are null on
            // the passes that did not produce them.
            SignalsNotPersisted: collection.SignalsNotPersisted,
            ScoreSnapshotsNotPersisted: null,
            // Spec 201 §1: a collect pass writes no report and no scoring config, so both stay null ("this
            // pass did not do that work") rather than a 0 that would claim clean writes that never happened.
            ReportsNotPersisted: null,
            ScoringConfigsNotPersisted: null);
        var runRecordWrite = await _runStore.WriteAsync(runRecord, ct).ConfigureAwait(false);
        RadarPipelineRunner.LogRunRecordNotPersisted(_logger, runRecordWrite);

        return pipelineResult;
    }
}
