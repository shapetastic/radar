using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Attention;
using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.DenominatorAudit;
using Radar.Application.EntityResolution;
using Radar.Application.Lifecycle;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Evaluation;
using Radar.Application.Pipeline;
using Radar.Application.Prices;
using Radar.Application.Replay;

namespace Radar.Worker;

/// <summary>
/// Thin host service: seeds the company watch-universe once, then runs <see cref="IRadarPipeline"/> —
/// once (then stops the application) or on a <see cref="PeriodicTimer"/> interval. Contains no pipeline
/// logic; all stage behaviour lives behind the injected interfaces. Takes time only from the injected
/// <see cref="TimeProvider"/> (no inline clock).
/// <para>
/// When the opt-in price-history acquisition is enabled (<c>Radar:Prices:Enabled</c>), the optional
/// <see cref="IPriceHistoryAcquirer"/> is invoked after seeding as a SEPARATE step, DISTINCT from and OUTSIDE
/// <see cref="IRadarPipeline"/> (AD-14): price is validation/reference data and must never enter the
/// evidence → signal → score path. When disabled the dependency is <c>null</c> and the step is skipped.
/// </para>
/// <para>
/// When the opt-in price-efficacy reporting is enabled (<c>Radar:Efficacy:Enabled</c>), the optional
/// <see cref="IEfficacyReportGenerator"/> is invoked AFTER each pipeline run (so the freshly-persisted snapshot
/// is included in the join) as a SEPARATE step, DISTINCT from and OUTSIDE <see cref="IRadarPipeline"/> (AD-14
/// read side): it READS score history + price and writes only efficacy artifacts. When disabled the dependency
/// is <c>null</c> and the step is skipped. The optional
/// <see cref="IAttentionArrivalScreenGenerator"/> (spec 169, <c>Radar:Efficacy:AttentionArrival:Enabled</c>),
/// AD-16's precommitted attention-arrival screen — which reads score history + run records + durable
/// signals/evidence and writes only its own artifacts — runs in the same step, immediately after it, with the
/// same read-only posture. The <see cref="IStrategyComparisonReportGenerator"/> (spec 140,
/// <c>Radar:Efficacy:Comparison:Enabled</c>) runs AFTER the attention screen (spec 170): its paired
/// comparison's AD-15 gate is COMPOSITE, so the Worker maps the screen result onto the neutral
/// <see cref="Ad15AttentionPrerequisite"/> and hands it in — <c>null</c> when the screen is disabled, which
/// fails closed as <c>ad16-screen-not-calculated</c>.
/// </para>
/// <para>
/// When the opt-in historical as-of replay is enabled (<c>Radar:Replay:Enabled</c>), the optional
/// <see cref="IReplayRunner"/> REPLACES the pipeline run entirely (spec 139): after seeding, the worker
/// replays the configured strategies over stored signals and stops. Replay is a read-only offline mode, so it
/// deliberately runs NONE of the other steps — no price acquisition (AD-14), no pipeline, no report, no
/// efficacy render. When disabled the dependency is <c>null</c> and the worker behaves exactly as before.
/// </para>
/// <para>
/// When a company filter is active (<c>Radar:Companies</c>, spec 161 — <c>collect</c> mode only), the
/// efficacy step is SKIPPED. Both the per-company render and the strategy leaderboard read the seeded company
/// universe, which under a filter holds only the named companies, so recomputing them would overwrite
/// whole-universe artifacts with a partial view. Unfiltered runs are unaffected in every mode: the dependency
/// is <c>null</c> and the step runs exactly as before.
/// </para>
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ICompanyUniverseSeeder _seeder;
    private readonly IRadarPipeline _pipeline;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly WorkerRunOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Worker> _logger;
    private readonly IPriceHistoryAcquirer? _priceHistoryAcquirer;
    private readonly IEfficacyReportGenerator? _efficacyReportGenerator;
    private readonly IReplayRunner? _replayRunner;
    private readonly IStrategyComparisonReportGenerator? _strategyComparisonGenerator;
    private readonly IAttentionArrivalScreenGenerator? _attentionArrivalGenerator;
    private readonly IScoreMoveDenominatorAuditGenerator? _denominatorAuditGenerator;
    private readonly CompanyFilter? _companyFilter;
    private readonly INewsObservationMigration? _newsObservationMigration;
    private readonly INewsRiskShadowGenerator? _newsRiskShadowGenerator;
    private readonly INewsRiskEvaluationGenerator? _newsRiskEvaluationGenerator;
    private readonly IOperatingCallStartupValidator? _operatingCallValidator;

    public Worker(
        ICompanyUniverseSeeder seeder,
        IRadarPipeline pipeline,
        IHostApplicationLifetime lifetime,
        WorkerRunOptions options,
        TimeProvider timeProvider,
        ILogger<Worker> logger,
        IPriceHistoryAcquirer? priceHistoryAcquirer = null,
        IEfficacyReportGenerator? efficacyReportGenerator = null,
        IReplayRunner? replayRunner = null,
        IStrategyComparisonReportGenerator? strategyComparisonGenerator = null,
        CompanyFilter? companyFilter = null,
        IAttentionArrivalScreenGenerator? attentionArrivalGenerator = null,
        IScoreMoveDenominatorAuditGenerator? denominatorAuditGenerator = null,
        INewsObservationMigration? newsObservationMigration = null,
        INewsRiskShadowGenerator? newsRiskShadowGenerator = null,
        INewsRiskEvaluationGenerator? newsRiskEvaluationGenerator = null,
        IOperatingCallStartupValidator? operatingCallValidator = null)
    {
        ArgumentNullException.ThrowIfNull(seeder);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _seeder = seeder;
        _pipeline = pipeline;
        _lifetime = lifetime;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _priceHistoryAcquirer = priceHistoryAcquirer;
        _efficacyReportGenerator = efficacyReportGenerator;
        _replayRunner = replayRunner;
        _strategyComparisonGenerator = strategyComparisonGenerator;
        _attentionArrivalGenerator = attentionArrivalGenerator;
        _denominatorAuditGenerator = denominatorAuditGenerator;
        _companyFilter = companyFilter;
        _newsObservationMigration = newsObservationMigration;
        _newsRiskShadowGenerator = newsRiskShadowGenerator;
        _newsRiskEvaluationGenerator = newsRiskEvaluationGenerator;
        _operatingCallValidator = operatingCallValidator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Which pass this process is (spec 144), logged ONCE up front so an unattended scheduled run's
            // log says whether it was the combined pass, a collect pass, a score pass or a replay. Purely
            // observational: the registered IRadarPipeline / IReplayRunner is what selects the behaviour.
            _logger.LogInformation("Radar run mode: {RunMode}.", RadarRunModes.Token(_options.Mode));

            // Spec 184 §2 rule 4: an invalid operating-calls file fails the run AT STARTUP, before seeding
            // and before any collection (mirroring StrategyIdentityGuard's "a misconfiguration costs no
            // collection"). Inert with a single configured strategy and when no calls file exists.
            if (_operatingCallValidator is not null)
            {
                await _operatingCallValidator.ValidateAsync(stoppingToken).ConfigureAwait(false);
            }

            // Seed the watch-universe once at startup (idempotent, AD-1) before any pipeline run.
            var seeded = await _seeder.SeedAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Seeded {Count} companies into the watch-universe.", seeded);

            // Explicit one-shot news observation migration (spec 177 §7): REPLACES the run entirely, like
            // a replay — it reads accrued raw news evidence (and, in retrospective mode, revisits saved
            // URLs through the safe reader) and writes only into the observation archive. Skipped entirely
            // (dependency null) unless Radar:NewsResearch:Migration:Enabled, so the default worker is
            // byte-for-byte unchanged; the composition root already rejects migration + replay together.
            if (_newsObservationMigration is not null)
            {
                var migration = await _newsObservationMigration.RunAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "News observation migration completed at {RunAt:o}: {Scanned} news evidence item(s) "
                        + "scanned, {Written} legacy observation(s) written ({Deduped} already archived, "
                        + "{Failed} failed, {Skipped} skipped); retrospective fetch: {RetroAttempted} "
                        + "attempted, {RetroWritten} written. No pipeline run happened.",
                    _timeProvider.GetUtcNow(),
                    migration.EvidenceScanned,
                    migration.LegacyWritten,
                    migration.LegacyDeduped,
                    migration.LegacyFailed,
                    migration.LegacySkipped,
                    migration.RetrospectiveAttempted,
                    migration.RetrospectiveWritten);
                _lifetime.StopApplication();
                return;
            }

            // Opt-in historical as-of replay (spec 139): REPLACES the run rather than adding to it. Placed
            // immediately after seeding — replay needs the company universe but must not run price
            // acquisition, the pipeline, the report or the efficacy render, because it is a read-only offline
            // re-scoring of ALREADY-STORED signals, not a run that observed anything new. Skipped entirely
            // (dependency null) unless Radar:Replay:Enabled, so the default worker is byte-for-byte unchanged.
            if (_replayRunner is not null)
            {
                var replay = await _replayRunner.RunAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Radar replay completed at {RunAt:o}: {SnapshotsWritten} snapshot(s) across "
                        + "{Strategies} strateg(ies) × {AsOfPoints} as-of point(s). No live store was written.",
                    _timeProvider.GetUtcNow(),
                    replay.SnapshotsWritten,
                    replay.Strategies,
                    replay.AsOfPoints);
                _lifetime.StopApplication();
                return;
            }

            // Opt-in price-history acquisition (AD-14): a SEPARATE step after seeding, DISTINCT from and OUTSIDE
            // IRadarPipeline. Skipped (dependency null) unless Radar:Prices:Enabled. Price is reference/validation
            // data — it never enters the evidence → signal → score path.
            if (_priceHistoryAcquirer is not null)
            {
                await _priceHistoryAcquirer.AcquireAsync(stoppingToken).ConfigureAwait(false);
            }

            if (_options.RunOnce)
            {
                await RunPipelineAndFollowUpsAsync(stoppingToken).ConfigureAwait(false);
                _lifetime.StopApplication();
                return;
            }

            using var timer = new PeriodicTimer(_options.Interval, _timeProvider);
            await RunPipelineAndFollowUpsAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunPipelineAndFollowUpsAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown — only swallow cancellations triggered by the host stopping token.
            // Cancellations from any other source are unexpected and propagate.
        }
    }

    private async Task RunPipelineAndFollowUpsAsync(CancellationToken ct)
    {
        var result = await RunPipelineAsync(ct).ConfigureAwait(false);
        // Spec 179 §2: the news-risk shadow read runs BEFORE the existing efficacy step, consuming the
        // exact section instances and durable run id the pipeline result carries.
        await RunNewsRiskShadowAsync(result, ct).ConfigureAwait(false);
        await RunEfficacyReportAsync(ct).ConfigureAwait(false);
    }

    private async Task<RadarPipelineResult> RunPipelineAsync(CancellationToken ct)
    {
        var result = await _pipeline.RunAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Radar pipeline run completed at {RunAt:o}: {EvidenceNew} new evidence, {SignalsApproved} signals approved, {CompaniesScored} companies scored, {SourcesFailed}/{SourcesChecked} sources unreadable, report {ReportId}.",
            _timeProvider.GetUtcNow(),
            result.EvidenceNew,
            result.SignalsApproved,
            result.CompaniesScored,
            result.SourcesFailed,
            result.SourcesChecked,
            result.ReportId);
        return result;
    }

    // Spec 179: the in-process news-risk shadow read — a SEPARATE step AFTER the pipeline run (and before
    // the efficacy step), OUTSIDE IRadarPipeline, following the efficacy-generator architecture. It consumes
    // the EXACT spec-176 structured sections the run's report builder produced (handed through the pipeline
    // result — never parsed Markdown, never a reopened score store) and reads NO price; the separate
    // read-only evaluator that joins frozen assessments to prices runs immediately after it. Both are
    // skipped entirely (dependencies null) unless Radar:NewsResearch:Shadow:Enabled in unfiltered full mode
    // with a resolvable reader. The generator owns its own failure handling (a shadow failure writes a named
    // FAILED artifact and never rolls back the run); the belt-and-braces catch here keeps even an unexpected
    // escape from aborting the host loop.
    private async Task RunNewsRiskShadowAsync(RadarPipelineResult result, CancellationToken ct)
    {
        if (_newsRiskShadowGenerator is null)
        {
            return;
        }

        try
        {
            await _newsRiskShadowGenerator
                .GenerateAsync(result.RunId, result.StrategySections, ct)
                .ConfigureAwait(false);

            if (_newsRiskEvaluationGenerator is not null)
            {
                await _newsRiskEvaluationGenerator.GenerateAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "News-risk shadow step failed unexpectedly; the Radar run itself is unaffected.");
        }
    }

    // Opt-in price-efficacy reporting (AD-14 read side): a SEPARATE step AFTER the pipeline run, DISTINCT from
    // and OUTSIDE IRadarPipeline, so the current run's freshly-persisted score snapshot is included in the join.
    // Skipped (dependency null) unless Radar:Efficacy:Enabled. It READS score history + price and writes only
    // efficacy artifacts — it never enters the evidence → signal → score path.
    private async Task RunEfficacyReportAsync(CancellationToken ct)
    {
        if (_efficacyReportGenerator is null
            && _strategyComparisonGenerator is null
            && _attentionArrivalGenerator is null
            && _denominatorAuditGenerator is null)
        {
            return;
        }

        // Spec 161: a company-FILTERED pass must not recompute whole-universe artifacts. Both generators join
        // through ICompanyRepository, which under a filter holds only the named companies, so running them
        // here would overwrite data/efficacy/*.svg|csv and strategy-leaderboard.{csv,md} with a partial view —
        // exactly the clobbering the collect-only guard exists to prevent. Skipped LOUDLY (one line), and only
        // when a filter is active: unfiltered behaviour in every mode is unchanged.
        if (_companyFilter is not null)
        {
            _logger.LogInformation(
                "Skipping the price-efficacy render, the strategy leaderboard and the attention-arrival "
                    + "screen: this run is a company-FILTERED collect pass (Radar:Companies = {Companies}). "
                    + "All three read the seeded company universe, so recomputing them from {CompanyCount} "
                    + "companies would overwrite whole-universe artifacts with a partial view. Run an "
                    + "unfiltered pass to refresh them.",
                _companyFilter.Describe(),
                _companyFilter.Tickers.Count);
            return;
        }

        if (_efficacyReportGenerator is not null)
        {
            await _efficacyReportGenerator.GenerateAsync(ct).ConfigureAwait(false);
        }

        // Spec 169's AD-16 attention-arrival screen: the same read-only posture, still OUTSIDE
        // IRadarPipeline. It runs after the pipeline so the run record and snapshots this run just persisted
        // are visible to it, and BEFORE the strategy comparison (spec 170) so the comparison's composite
        // AD-15 gate can consume its outcome. Skipped (dependency null) unless Radar:Efficacy:Enabled AND
        // Radar:Efficacy:AttentionArrival:Enabled — and never in a replay run, which replaces the pipeline
        // entirely and returns before this method is reached.
        AttentionArrivalScreenResult? attentionScreen = null;
        if (_attentionArrivalGenerator is not null)
        {
            attentionScreen = await _attentionArrivalGenerator.GenerateAsync(ct).ConfigureAwait(false);
        }

        // Spec 140's strategy-vs-price comparison: the same AD-14 read-side posture, still OUTSIDE
        // IRadarPipeline. Skipped (dependency null) unless Radar:Efficacy:Enabled AND
        // Radar:Efficacy:Comparison:Enabled. It ranks Radar's own strategies against subsequent price
        // movement and writes one leaderboard artifact pair; it promotes nothing. The Worker is the ONE
        // composition point that can see both the Attention result and the Comparison seam, so the spec-170
        // mapping happens here: screen result → neutral Claims prerequisite. A null screen (generator
        // disabled) is passed through as null and fails closed inside the gate — never invented, never
        // defaulted to "calculated".
        if (_strategyComparisonGenerator is not null)
        {
            var attentionPrerequisite = attentionScreen is null
                ? null
                : Ad15AttentionPrerequisiteMap.From(attentionScreen);
            await _strategyComparisonGenerator
                .GenerateAsync(attentionPrerequisite, ct)
                .ConfigureAwait(false);
        }

        // Spec 172's score-move vs evidence-denominator audit: the same read-only posture, still OUTSIDE
        // IRadarPipeline. Skipped (dependency null) unless Radar:Efficacy:Enabled AND
        // Radar:Efficacy:DenominatorAudit:Enabled (DEFAULT OFF — a one-shot diagnostic). It reads persisted
        // snapshots + stored evidence links, changes no score, reads no price, and writes only the audit
        // artifact pair under data/audits/. A replay run replaces the pipeline entirely and returns before
        // this method is reached; a company-filtered pass returned above.
        if (_denominatorAuditGenerator is not null)
        {
            await _denominatorAuditGenerator.GenerateAsync(ct).ConfigureAwait(false);
        }
    }
}
