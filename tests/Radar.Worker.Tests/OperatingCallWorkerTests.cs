using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Lifecycle;
using Radar.Application.Pipeline;
using Radar.Infrastructure.FileSystem;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 184 §2 rule 4, Worker side: an invalid operating-calls file fails AT STARTUP — before seeding and
/// before any collection (the StrategyIdentityGuard posture) — and the composed graph wires the FILE-backed
/// call/facts sources plus the startup validator.
/// </summary>
public sealed class OperatingCallWorkerTests
{
    private sealed class CountingSeeder : ICompanyUniverseSeeder
    {
        public int SeedCount { get; private set; }

        public Task<int> SeedAsync(CancellationToken ct)
        {
            SeedCount++;
            return Task.FromResult(0);
        }
    }

    private sealed class CountingPipeline : IRadarPipeline
    {
        public int RunCount { get; private set; }

        public Task<RadarPipelineResult> RunAsync(CancellationToken ct)
        {
            RunCount++;
            return Task.FromResult(new RadarPipelineResult(
                0, 0, 0, 0, 0, 0, 0, null, 0, 0, CollectionSummary.Empty));
        }
    }

    private sealed class ThrowingValidator : IOperatingCallStartupValidator
    {
        public Task ValidateAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Operating-calls file 'x': invalid for the test.");
    }

    [Fact]
    public async Task InvalidCallsFile_FailsStartup_BeforeSeedingAndBeforeAnyPipelineRun()
    {
        var seeder = new CountingSeeder();
        var pipeline = new CountingPipeline();

        var worker = new Worker(
            seeder,
            pipeline,
            new FakeLifetime(),
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            operatingCallValidator: new ThrowingValidator());

        await worker.StartAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.ExecuteTask!);

        Assert.Contains("Operating-calls file", ex.Message);
        Assert.Equal(0, seeder.SeedCount);   // a misconfiguration costs no seeding…
        Assert.Equal(0, pipeline.RunCount);  // …and no collection
    }

    [Fact]
    public void ComposedGraph_WiresTheFileBackedSourcesAndTheStartupValidator()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        using var provider = services.BuildServiceProvider();

        // The Worker registers the file-backed pair BEFORE AddRadarApplicationServices, so the library's
        // inert defaults lose (the spec-150 lesson: a silently-inert production wiring must be impossible).
        Assert.IsType<FileOperatingCallSource>(provider.GetRequiredService<IOperatingCallSource>());
        Assert.IsType<FileStrategyEvidenceFactsSource>(
            provider.GetRequiredService<IStrategyEvidenceFactsSource>());
        Assert.IsType<OperatingCallStartupValidator>(
            provider.GetRequiredService<IOperatingCallStartupValidator>());
    }
}
