using Radar.Application.Efficacy;
using Radar.Application.EntityResolution;
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
/// is <c>null</c> and the step is skipped.
/// </para>
/// <para>
/// When the opt-in historical as-of replay is enabled (<c>Radar:Replay:Enabled</c>), the optional
/// <see cref="IReplayRunner"/> REPLACES the pipeline run entirely (spec 139): after seeding, the worker
/// replays the configured strategies over stored signals and stops. Replay is a read-only offline mode, so it
/// deliberately runs NONE of the other steps — no price acquisition (AD-14), no pipeline, no report, no
/// efficacy render. When disabled the dependency is <c>null</c> and the worker behaves exactly as before.
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

    public Worker(
        ICompanyUniverseSeeder seeder,
        IRadarPipeline pipeline,
        IHostApplicationLifetime lifetime,
        WorkerRunOptions options,
        TimeProvider timeProvider,
        ILogger<Worker> logger,
        IPriceHistoryAcquirer? priceHistoryAcquirer = null,
        IEfficacyReportGenerator? efficacyReportGenerator = null,
        IReplayRunner? replayRunner = null)
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Seed the watch-universe once at startup (idempotent, AD-1) before any pipeline run.
            var seeded = await _seeder.SeedAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Seeded {Count} companies into the watch-universe.", seeded);

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
                await RunPipelineAsync(stoppingToken).ConfigureAwait(false);
                await RunEfficacyReportAsync(stoppingToken).ConfigureAwait(false);
                _lifetime.StopApplication();
                return;
            }

            using var timer = new PeriodicTimer(_options.Interval, _timeProvider);
            await RunPipelineAsync(stoppingToken).ConfigureAwait(false);
            await RunEfficacyReportAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunPipelineAsync(stoppingToken).ConfigureAwait(false);
                await RunEfficacyReportAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown — only swallow cancellations triggered by the host stopping token.
            // Cancellations from any other source are unexpected and propagate.
        }
    }

    private async Task RunPipelineAsync(CancellationToken ct)
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
    }

    // Opt-in price-efficacy reporting (AD-14 read side): a SEPARATE step AFTER the pipeline run, DISTINCT from
    // and OUTSIDE IRadarPipeline, so the current run's freshly-persisted score snapshot is included in the join.
    // Skipped (dependency null) unless Radar:Efficacy:Enabled. It READS score history + price and writes only
    // efficacy artifacts — it never enters the evidence → signal → score path.
    private async Task RunEfficacyReportAsync(CancellationToken ct)
    {
        if (_efficacyReportGenerator is not null)
        {
            await _efficacyReportGenerator.GenerateAsync(ct).ConfigureAwait(false);
        }
    }
}
