using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;

namespace Radar.Worker;

/// <summary>
/// Thin host service: seeds the company watch-universe once, then runs <see cref="IRadarPipeline"/> —
/// once (then stops the application) or on a <see cref="PeriodicTimer"/> interval. Contains no pipeline
/// logic; all stage behaviour lives behind the injected interfaces. Takes time only from the injected
/// <see cref="TimeProvider"/> (no inline clock).
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ICompanyUniverseSeeder _seeder;
    private readonly IRadarPipeline _pipeline;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly WorkerRunOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Worker> _logger;

    public Worker(
        ICompanyUniverseSeeder seeder,
        IRadarPipeline pipeline,
        IHostApplicationLifetime lifetime,
        WorkerRunOptions options,
        TimeProvider timeProvider,
        ILogger<Worker> logger)
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Seed the watch-universe once at startup (idempotent, AD-1) before any pipeline run.
            var seeded = await _seeder.SeedAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Seeded {Count} companies into the watch-universe.", seeded);

            if (_options.RunOnce)
            {
                await RunPipelineAsync(stoppingToken).ConfigureAwait(false);
                _lifetime.StopApplication();
                return;
            }

            using var timer = new PeriodicTimer(_options.Interval, _timeProvider);
            await RunPipelineAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunPipelineAsync(stoppingToken).ConfigureAwait(false);
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
}
