using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Radar.Application.Collectors;
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
    public void Default_ScoringWindow_Is60Days()
    {
        using var provider = BuildProvider();

        var scoring = provider.GetRequiredService<ScoringOptions>();

        Assert.Equal(TimeSpan.FromDays(60), scoring.Window);
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
    public void Graph_Resolves_WithSingleRssCollector()
    {
        using var provider = BuildProvider(("Radar:Collectors:0", "rss"));

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.Single(provider.GetServices<IEvidenceCollector>());
    }

    [Fact]
    public void Graph_Resolves_WithDefaultCollectors_WhenNoneConfigured()
    {
        // No Radar:Collectors key → the RadarWorkerOptions default ["rss"] is used.
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.NotNull(provider.GetService<IEvidenceCollector>());
    }

    [Fact]
    public void Graph_Resolves_WithTwoCollectors_Additively()
    {
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "localfile"));

        Assert.NotNull(provider.GetService<IRadarPipeline>());

        // Both enabled kinds compose into the IEnumerable<IEvidenceCollector> the runner consumes.
        Assert.Equal(2, provider.GetServices<IEvidenceCollector>().Count());
    }

    [Fact]
    public void DuplicateCollectorKind_RegistersOnce()
    {
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "rss"));

        // The defensive HashSet de-dupe means the repeated kind registers a single collector.
        Assert.Single(provider.GetServices<IEvidenceCollector>());
    }

    [Fact]
    public void UnknownCollectorKind_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("Radar:Collectors:0", "bogus")));
        Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
    }

    // NOTE: the empty-list fail-fast in AddRadarWorker (Collectors null/empty ->
    // InvalidOperationException) is intentionally not covered by a config-driven test here: the
    // Microsoft.Extensions.Configuration binder only replaces the RadarWorkerOptions.Collectors default
    // (["rss"]) when the Radar:Collectors section has indexed children, so an in-memory configuration
    // cannot express a non-null empty list — omitting the key yields the ["rss"] default, not an empty
    // list. The unknown-kind test above covers the sibling fail-fast branch, and the default/single/two/
    // duplicate tests cover the additive enablement paths.

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
