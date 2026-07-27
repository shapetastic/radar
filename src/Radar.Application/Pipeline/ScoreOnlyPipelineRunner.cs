using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Reporting;
using Radar.Application.Scoring;

namespace Radar.Application.Pipeline;

/// <summary>
/// The standalone <c>score</c> pass (spec 144): stage 6 (and optionally stage 7) over whatever has already
/// accrued, with <b>no collection and no AI read</b>. Adding or re-running a strategy therefore costs a
/// scoring pass — no collector runs, so no SEC fair-access exposure, no GDELT/Google-News traffic and no AI
/// spend.
/// <para>
/// <b>That is not the same as "issues no request".</b> Price acquisition is deliberately OUTSIDE
/// <see cref="IRadarPipeline"/> (AD-14) and is gated on <c>Radar:Prices:Enabled</c> alone, independent of the
/// run mode — so with a configuration that enables it (the shipped default profile does) the host still
/// fetches daily price history per ticker around a score pass. Price is reference/validation data and never
/// enters evidence → signal → score, so this changes nothing about what a score pass computes; it does mean a
/// frequently-repeated score pass should turn <c>Radar:Prices:Enabled</c> off.
/// </para>
/// <para>
/// <b>The absence of collection is structural, not a rule.</b> This type takes no
/// <see cref="IEvidenceCollector"/>, no <c>CollectedEvidenceMapper</c>, no extractor, no resolver, no
/// reviewer, no raw-evidence store and no <c>IDirectionalFilingSignalSource</c>. In <c>score</c> mode the
/// composition root additionally registers no collector at all, so none is even constructed — construction is
/// what opens the HttpClients.
/// </para>
/// <para>
/// <b>It writes the LIVE series</b> — it is the record of what Radar thinks now — which is precisely what
/// separates it from a spec-139 replay (a hypothesis about what Radar would have thought, written to the
/// replay root). A score pass whose as-of instant is in the PAST is a replay, so it is rejected rather than
/// allowed to back-date the live series.
/// </para>
/// <para>
/// <b>How this pass records provenance (spec 147).</b> No collector is registered, but the collector
/// VOCABULARY is: the composition root resolves <c>Radar:Collectors</c> in every mode and registers the
/// name-only <c>EnabledCollectorVocabulary</c>, so
/// <see cref="ISignalSourceDescriptor.CollectionProvenance"/> records the CONFIGURED collector set plus an
/// explicit <c>collection=none-this-pass;</c> marker — <c>collectors=rss,sec-edgar;collection=none-this-pass;</c>
/// — instead of the bare <c>collectors=;</c> it used to write, which claimed no collector existed over
/// evidence a <c>collect</c> pass had genuinely gathered from several. It stays recorded provenance, hashed
/// into nothing, so no fingerprint moves and no component score changes. A <c>radar-formula-v9</c> strategy
/// declaring collector channels therefore starts and scores in <c>score</c> mode, with the spec-146 guard
/// (a channel may only name a collector in the vocabulary) unweakened. Spec 139's replay is deliberately
/// NOT marked: it registers real collectors and its <c>replay ⊆ forward</c> invariant compares snapshots
/// field for field.
/// </para>
/// </summary>
public sealed class ScoreOnlyPipelineRunner : IRadarPipeline
{
    private readonly IScoringPass _scoringPass;
    private readonly ICompanyRepository _companyRepository;
    private readonly IScoringStrategyFactory _scoringStrategies;
    private readonly IScoringConfigStore _scoringConfigStore;
    private readonly IWeeklyReportBuilder _reportBuilder;
    private readonly IReportFileWriter _reportFileWriter;
    private readonly IPipelineRunStore _runStore;
    private readonly PipelineOptions _options;
    private readonly ScoringPassOptions _scoringPassOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScoreOnlyPipelineRunner> _logger;

    public ScoreOnlyPipelineRunner(
        IScoringPass scoringPass,
        ICompanyRepository companyRepository,
        IScoringStrategyFactory scoringStrategies,
        IScoringConfigStore scoringConfigStore,
        IWeeklyReportBuilder reportBuilder,
        IReportFileWriter reportFileWriter,
        IPipelineRunStore runStore,
        PipelineOptions options,
        ScoringPassOptions scoringPassOptions,
        TimeProvider timeProvider,
        ILogger<ScoreOnlyPipelineRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(scoringPass);
        ArgumentNullException.ThrowIfNull(companyRepository);
        ArgumentNullException.ThrowIfNull(scoringStrategies);
        ArgumentNullException.ThrowIfNull(scoringConfigStore);
        ArgumentNullException.ThrowIfNull(reportBuilder);
        ArgumentNullException.ThrowIfNull(reportFileWriter);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scoringPassOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scoringPass = scoringPass;
        _companyRepository = companyRepository;
        _scoringStrategies = scoringStrategies;
        _scoringConfigStore = scoringConfigStore;
        _reportBuilder = reportBuilder;
        _reportFileWriter = reportFileWriter;
        _runStore = runStore;
        _options = options;
        _scoringPassOptions = scoringPassOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RadarPipelineResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Stage 0 (spec 141): the strategy-identity tripwire, kept first here too. A score pass runs no
        // collector and no AI read — which, as the type remarks spell out, is NOT the same as issuing no
        // request, since price acquisition is gated independently and sits outside this pipeline — but it
        // DOES write the live series, so an in-place strategy edit must fail before a single snapshot lands
        // under the old name.
        await StrategyIdentityGuard
            .VerifyAsync(_scoringStrategies.Runtimes, _scoringConfigStore, _logger, ct)
            .ConfigureAwait(false);

        // ONE clock read for the whole pass (AD-7: one run, one instant), and the same read the guard below
        // compares against — so an unconfigured (⇒ "now") as-of can never trip its own past-dating check on a
        // clock that advanced between two reads.
        var nowUtc = _timeProvider.GetUtcNow();
        var asOfUtc = _scoringPassOptions.AsOfUtc ?? nowUtc;

        // A PAST-DATED standalone score is a REPLAY (spec 139) and must never write the live series: the
        // live series is the record of what Radar thinks NOW, and back-dating it would silently rewrite
        // accrued history with a hypothesis. Thrown BEFORE anything is loaded or written, so a rejected pass
        // leaves no trace whatsoever.
        if (asOfUtc < nowUtc)
        {
            throw new InvalidOperationException(
                $"Radar:Score:AsOfUtc is '{asOfUtc:o}', which is in the past (now is '{nowUtc:o}'). A "
                    + "standalone score pass writes the LIVE score series — the record of what Radar thinks "
                    + "now — so it may not be back-dated. Scoring a historical as-of instant is a REPLAY: "
                    + "enable Radar:Replay (Radar:Replay:Enabled/From/To/Step), which re-scores stored "
                    + "signals through the same engine and writes only under Radar:ReplayDirectory. Clear "
                    + "Radar:Score:AsOfUtc to score at the current instant.");
        }

        var companies = await _companyRepository.GetAllAsync(ct).ConfigureAwait(false);

        var scoring = await _scoringPass.RunAsync(companies, asOfUtc, ct).ConfigureAwait(false);

        // Stage 7: optional report, exactly as the combined run builds it. No collection happened this pass,
        // so the transparency footer gets an EMPTY collection summary and no collection-health report — both
        // already supported by IWeeklyReportBuilder — rather than a fabricated or a stale one.
        Guid? reportId = null;
        if (_options.GenerateReport)
        {
            var report = await _reportBuilder
                .GenerateAsync(asOfUtc, CollectionSummary.Empty, health: null, ct)
                .ConfigureAwait(false);
            await _reportFileWriter.WriteAsync(report.Report, ct).ConfigureAwait(false);
            reportId = report.Report.Id;
        }

        _logger.LogInformation(
            "Score-only run complete: {CompaniesScored} companies scored by the primary of {StrategyCount} "
                + "strategies at {AsOfUtc:o}, report {ReportId}. Nothing was collected and no AI read ran.",
            scoring.CompaniesScored,
            scoring.Strategies.Count,
            asOfUtc,
            reportId?.ToString() ?? "none");

        var pipelineResult = new RadarPipelineResult(
            // Nothing was collected this pass: every collection counter is honestly zero and the collection
            // summary is Empty. A reader of the run log sees "0 sources checked", not "0 sources failed out
            // of the usual N".
            EvidenceCollected: 0,
            EvidenceNew: 0,
            SignalsExtracted: 0,
            SignalsValid: 0,
            SignalsApproved: 0,
            SignalsNeedingReview: 0,
            CompaniesScored: scoring.CompaniesScored,
            ReportId: reportId,
            SourcesChecked: 0,
            SourcesFailed: 0,
            Collection: CollectionSummary.Empty);

        var runRecord = new PipelineRunRecord(
            Id: Guid.NewGuid(),
            CreatedAtUtc: asOfUtc,
            // No collector ran — and in score mode none is even registered, so this is a fact about the pass
            // rather than a placeholder.
            Collectors: [],
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
            // No collection ⇒ no collection-health findings were produced. Null is the record's
            // "unrecorded" value; an empty list would claim a clean reconciliation that never ran.
            CollectionWarnings: null,
            Strategies: scoring.Strategies,
            PrimaryStrategy: scoring.PrimaryStrategy);
        await _runStore.WriteAsync(runRecord, ct).ConfigureAwait(false);

        return pipelineResult;
    }
}
