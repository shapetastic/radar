using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Radar.Application.Ai;
using Radar.Application.Collectors;
using Radar.Application.Filings;
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
    public void Graph_Resolves_WithSecCollector_WhenUserAgentConfigured()
    {
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "sec"),
            ("Radar:Sec:UserAgent", "Radar Research test@example.com"));

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.Equal(2, provider.GetServices<IEvidenceCollector>().Count());
    }

    [Fact]
    public void SecCollector_WithoutUserAgent_FailsFast()
    {
        // A blank Radar:Sec:UserAgent must fail fast: every SEC request 403s without a compliant UA.
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("Radar:Collectors:0", "sec")));
        Assert.Contains("User-Agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_Resolves_WithUsaSpendingCollector()
    {
        // The USASpending API needs no key/User-Agent, so the collector enables with just the kind and the
        // RadarWorkerOptions defaults (contracts group A/B/C/D, 365-day lookback, cap 25).
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "usaspending"));

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.Equal(2, provider.GetServices<IEvidenceCollector>().Count());
    }

    [Fact]
    public void Graph_Resolves_WithNewsCollector()
    {
        // The GDELT DOC API needs no key/User-Agent, so the news collector enables with just the kind and the
        // RadarWorkerOptions defaults (2w window, cap 25, English-only, 3s pacing, 1 retry).
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "news"));

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.Equal(2, provider.GetServices<IEvidenceCollector>().Count());
    }

    [Fact]
    public void Graph_Resolves_WithNewsSearchCollector()
    {
        // Google News RSS needs no key/User-Agent, so the newssearch collector enables with just the kind and
        // the RadarWorkerOptions defaults (cap 25, English-only, 1s pacing). It is a DISTINCT kind from "news".
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "newssearch"));

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.Equal(2, provider.GetServices<IEvidenceCollector>().Count());
    }

    [Fact]
    public void Graph_Resolves_WithNewsAndNewsSearch_Independently()
    {
        // The GDELT "news" collector and the Google News "newssearch" collector are independently enable-able
        // and coexist as distinct IEvidenceCollector registrations (alongside the rss collector).
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "rss"),
            ("Radar:Collectors:1", "news"),
            ("Radar:Collectors:2", "newssearch"));

        Assert.NotNull(provider.GetService<IRadarPipeline>());
        Assert.Equal(3, provider.GetServices<IEvidenceCollector>().Count());
    }

    [Fact]
    public void DefaultCollectors_DoNotRegisterNewsSearch()
    {
        // The default ["rss"] config registers exactly one collector; newssearch is opt-in.
        using var provider = BuildProvider(("Radar:Collectors:0", "rss"));

        Assert.Single(provider.GetServices<IEvidenceCollector>());
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

    [Fact]
    public void WhitespaceCollectorEntry_FailsFast_WithClearMessage()
    {
        // A blank entry must fail fast with a dedicated message rather than falling through to the
        // unknown-kind branch as the unhelpful "kind ' ' is not supported".
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("Radar:Collectors:0", "   ")));
        Assert.Contains("must not be null, empty, or whitespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectorKind_IsTrimmed_BeforeMatching()
    {
        // Surrounding whitespace is normalized away so a padded but otherwise valid kind still
        // registers its collector rather than tripping the unknown-kind branch.
        using var provider = BuildProvider(("Radar:Collectors:0", "  rss  "));

        Assert.Single(provider.GetServices<IEvidenceCollector>());
    }

    // NOTE: the empty-list fail-fast in AddRadarWorker (Collectors null/empty ->
    // InvalidOperationException) is intentionally not covered by a config-driven test here: the
    // Microsoft.Extensions.Configuration binder only replaces the RadarWorkerOptions.Collectors default
    // (["rss"]) when the Radar:Collectors section has indexed children, so an in-memory configuration
    // cannot express a non-null empty list — omitting the key yields the ["rss"] default, not an empty
    // list. The unknown-kind test above covers the sibling fail-fast branch, and the default/single/two/
    // duplicate tests cover the additive enablement paths.

    [Fact]
    public void DefaultConfig_RegistersNoAiClient_AiDisabled()
    {
        // A blank Radar:Ai:Provider (the default) means AI is opt-in disabled: the graph must register
        // neither the factory nor the client, so the default pipeline is byte-for-byte unchanged. The
        // directional filing enrichment (source + earnings reader) rides the same gate, so neither is
        // registered and the runner's optional dependency stays null.
        using var provider = BuildProvider();

        Assert.Null(provider.GetService<IChatClientFactory>());
        Assert.Null(provider.GetService<IChatClient>());
        Assert.Null(provider.GetService<IFilingAnalyzer>());
        Assert.Null(provider.GetService<IDirectionalFilingSignalSource>());

        // The pipeline still resolves — its optional IDirectionalFilingSignalSource dependency defaults to
        // null when the service is absent.
        Assert.NotNull(provider.GetService<IRadarPipeline>());
    }

    [Fact]
    public void AiProviderOllama_RegistersFactoryAndClient_OptInGateFlips()
    {
        // Directional filing signals need a compliant SEC User-Agent (to fetch the EX-99.1 body), so the
        // gate that enables AI also wires the earnings reader — supply the UA.
        using var provider = BuildProvider(
            ("Radar:Ai:Provider", "ollama"),
            ("Radar:Ai:Model", "llama3.1"),
            ("Radar:Sec:UserAgent", "Radar Research test@example.com"));

        Assert.NotNull(provider.GetService<IChatClientFactory>());
        Assert.NotNull(provider.GetService<IChatClient>());
        Assert.NotNull(provider.GetService<IFilingAnalyzer>());

        // The directional filing signal source is registered and the pipeline resolves the optional source
        // (case 15). Resolving the source also proves the internal earnings reader is registered — the
        // source's constructor requires it, so resolution would throw otherwise.
        Assert.NotNull(provider.GetService<IDirectionalFilingSignalSource>());
        Assert.NotNull(provider.GetService<IRadarPipeline>());
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
