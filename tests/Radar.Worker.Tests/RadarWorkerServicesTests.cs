using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;

namespace Radar.Worker.Tests;

public sealed class RadarWorkerServicesTests
{
    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Graph_Resolves_PipelineSeederAndHostedWorker()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.NotNull(provider.GetService<ICompanyUniverseSeeder>());

        var worker = provider.GetServices<IHostedService>().OfType<Worker>().Single();
        Assert.NotNull(worker);
    }

    [Fact]
    public void Configuration_OverridesLibraryDefaults_ForScoringAndReportOptions()
    {
        using var provider = BuildProvider(
            ("Radar:ScoringWindowDays", "14"),
            ("Radar:ReportMaxItems", "5"));

        var scoring = provider.GetRequiredService<ScoringOptions>();
        var report = provider.GetRequiredService<WeeklyReportOptions>();

        Assert.Equal(TimeSpan.FromDays(14), scoring.Window);
        Assert.Equal(5, report.MaxItems);
    }

    [Fact]
    public void Configuration_GenerateReportFalse_FlowsThroughToPipelineOptions()
    {
        using var provider = BuildProvider(("Radar:GenerateReport", "false"));

        var pipelineOptions = provider.GetRequiredService<PipelineOptions>();

        Assert.False(pipelineOptions.GenerateReport);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
