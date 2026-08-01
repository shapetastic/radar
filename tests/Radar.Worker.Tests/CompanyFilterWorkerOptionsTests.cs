using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Infrastructure.Sources;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 161 — the <c>Radar:Companies</c> config surface: the collect-only mode guard, the seed-source
/// decoration, the byte-identical off-switch, and the end-to-end composition property that a filtered
/// <c>collect</c> pass seeds only the named companies (and therefore only their source feeds).
/// </summary>
public sealed class CompanyFilterWorkerOptionsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _seedPath;

    public CompanyFilterWorkerOptionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _seedPath = Path.Combine(_tempDir, "companies.json");
        File.WriteAllText(_seedPath, ThreeCompanySeed);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private const string ThreeCompanySeed = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Cass Information Systems",
              "ticker": "CASS",
              "aliases": ["Cass"],
              "sourceFeeds": [
                { "type": "rss", "name": "Cass IR", "url": "https://example.com/cass.xml" }
              ]
            },
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "IDT Corporation",
              "ticker": "IDT",
              "aliases": ["IDT"],
              "sourceFeeds": [
                { "type": "rss", "name": "IDT IR", "url": "https://example.com/idt.xml" }
              ]
            },
            {
              "id": "33333333-3333-3333-3333-333333333333",
              "name": "Caterpillar Inc.",
              "ticker": "CAT",
              "aliases": ["Caterpillar"],
              "sourceFeeds": [
                { "type": "rss", "name": "CAT IR", "url": "https://example.com/cat.xml" }
              ]
            }
          ]
        }
        """;

    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings) =>
        BuildProvider(null, settings);

    /// <summary>
    /// Builds the real composition-root graph, optionally letting a caller add its own registrations AFTER
    /// <c>AddRadarWorker</c> (last registration wins for a single-service resolve) so a composed end-to-end
    /// test can observe an optional Worker dependency without reflection.
    /// </summary>
    private static ServiceProvider BuildProvider(
        Action<IServiceCollection>? configureExtra, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        configureExtra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildFilteredCollectProvider(params string[] tickers)
    {
        (string, string)[] settings =
        [
            ("Radar:RunMode", "collect"),
            ("Radar:CompanySeedFilePath", _seedPath),
            .. tickers.Select((t, i) => ($"Radar:Companies:{i}", t)),
        ];

        return BuildProvider(settings);
    }

    // ---- the mode guard: the filter is collect-only ---------------------------------------------------

    [Theory]
    [InlineData("full")]
    [InlineData("score")]
    [InlineData("replay")]
    public void FilterOutsideCollectMode_FailsFast_NamingBothKeys(string mode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(
            ("Radar:RunMode", mode),
            ("Radar:Companies:0", "CASS"),
            // Supplied so a replay-mode run reaches the company guard rather than failing on a missing range.
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03")));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Radar:RunMode", ex.Message, StringComparison.Ordinal);
        Assert.Contains(mode, ex.Message, StringComparison.Ordinal);
        // The reason, in one sentence, and the remedy.
        Assert.Contains("weekly report", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Radar:RunMode=collect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsetRunMode_IsFullMode_AndStillRejectsTheFilter()
    {
        // The default mode is "full" — the guard must not be reachable only via an explicit RunMode value.
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("Radar:Companies:0", "CASS")));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Radar:RunMode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterWithCollectMode_Starts()
    {
        using var provider = BuildFilteredCollectProvider("CASS", "IDT");

        Assert.IsType<CollectOnlyPipelineRunner>(provider.GetRequiredService<IRadarPipeline>());
        Assert.Equal(["CASS", "IDT"], provider.GetRequiredService<CompanyFilter>().Tickers);
    }

    [Fact]
    public void BlankFilterEntry_FailsFast_InCollectMode()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFilteredCollectProvider("CASS", "   "));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("must not be null, empty, or whitespace", ex.Message, StringComparison.Ordinal);
    }

    // ---- the off-switch is ABSENCE: byte-identical in every mode --------------------------------------

    [Theory]
    [InlineData("full")]
    [InlineData("collect")]
    [InlineData("score")]
    [InlineData("replay")]
    public async Task NoFilter_ResolvesTheUndecoratedSeedSource_InEveryMode(string mode)
    {
        using var provider = BuildProvider(
            ("Radar:RunMode", mode),
            ("Radar:CompanySeedFilePath", _seedPath),
            ("Radar:Replay:From", "2026-05-01"),
            ("Radar:Replay:To", "2026-05-03"));

        // Same registered implementation type as a deployment that never heard of Radar:Companies…
        var seedSource = provider.GetRequiredService<ICompanySeedSource>();
        Assert.IsType<LocalFileCompanySeedSource>(seedSource);
        Assert.Single(provider.GetServices<ICompanySeedSource>());

        // …nothing filter-related registered at all…
        Assert.Null(provider.GetService<CompanyFilter>());

        // …and the same seed content: all three companies with all three feeds.
        var seed = await seedSource.GetSeedAsync(default);
        Assert.Equal(["CASS", "IDT", "CAT"], seed.Companies.Select(c => c.Ticker));
        Assert.Equal(3, seed.SourceFeeds.Count);
    }

    [Fact]
    public async Task EmptyCompaniesSection_IsTreatedAsNoFilter()
    {
        // An empty list is not "filter to nothing" — the off-switch is absence, and an absent/empty section
        // is the same thing.
        using var provider = BuildProvider(("Radar:CompanySeedFilePath", _seedPath));

        Assert.Null(provider.GetService<CompanyFilter>());
        var seed = await provider.GetRequiredService<ICompanySeedSource>().GetSeedAsync(default);
        Assert.Equal(3, seed.Companies.Count);
    }

    // ---- composition: a filtered collect pass seeds only the named companies --------------------------

    [Fact]
    public void FilteredCollectMode_DecoratesTheSeedSource()
    {
        using var provider = BuildFilteredCollectProvider("CASS");

        Assert.IsType<FilteredCompanySeedSource>(provider.GetRequiredService<ICompanySeedSource>());
        // The decoration REPLACES the registration; nothing dangles in the IEnumerable view.
        Assert.Single(provider.GetServices<ICompanySeedSource>());
    }

    [Fact]
    public async Task FilteredCollectMode_RegistersCollectors_ButSeedsOnlyTheNamedCompanies()
    {
        // The spec's composition criterion: collect mode DOES register collectors (unlike score mode), and
        // the filter bites at the seeded repository — so the collection context's source feeds are only the
        // retained company's, and an excluded company's feed is never fetched.
        using var provider = BuildFilteredCollectProvider("cass");

        Assert.NotEmpty(provider.GetServices<IEvidenceCollector>());

        await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);

        var companies = provider.GetRequiredService<ICompanyRepository>();
        var seeded = await companies.GetAllAsync(default);
        var seededCompany = Assert.Single(seeded);
        Assert.Equal("CASS", seededCompany.Ticker);

        var feeds = await companies.GetSourceFeedsAsync(default);
        var feed = Assert.Single(feeds);
        Assert.Equal(seededCompany.Id, feed.CompanyId);
        Assert.Equal("https://example.com/cass.xml", feed.Url);

        var aliases = await companies.GetAliasesAsync(default);
        Assert.All(aliases, a => Assert.Equal(seededCompany.Id, a.CompanyId));
    }

    // ---- the filter REACHES its two consumers through DI (composed, end to end) ----------------------

    /// <summary>
    /// The wiring guard for spec 161's two guarantees, both of which ride an OPTIONAL-NULLABLE constructor
    /// parameter and would therefore fail SILENTLY if DI stopped supplying them (the spec-146/150 review's
    /// "silently-null optional dependency while every test stays green" class):
    /// <list type="number">
    /// <item>§4 — <see cref="CollectOnlyPipelineRunner"/> stamps the resolved filter on the run record, so a
    /// partial pass is never mistakable for a full one; and</item>
    /// <item><see cref="Worker"/> skips the efficacy render + strategy leaderboard, so a 2-company pass never
    /// overwrites the whole-universe <c>strategy-leaderboard.{csv,md}</c>.</item>
    /// </list>
    /// Both are asserted BEHAVIOURALLY over the REAL <c>AddRadarWorker</c> graph — no reflection, no
    /// hand-built subject — by running the composed hosted worker end to end and reading back what it wrote.
    /// The <c>localfile</c> collector is used deliberately: it is the only collector that issues no network
    /// request, and it collects nothing here, which is irrelevant — the run record is written either way.
    /// </summary>
    [Fact]
    public async Task ComposedFilteredCollectRun_StampsTheFilterOnTheRunRecord_AndSkipsEfficacy()
    {
        var callLog = new List<string>();

        using var provider = BuildProvider(
            services =>
            {
                // Radar:Efficacy:Enabled is false here, so the composition root registers NEITHER generator —
                // these recorders are what the composed Worker resolves for its optional parameters. If the
                // Worker ever stopped receiving the filter, they would be invoked and this test would fail.
                services.AddSingleton<IEfficacyReportGenerator>(new RecordingEfficacyGenerator(callLog));
                services.AddSingleton<IStrategyComparisonReportGenerator>(
                    new RecordingStrategyComparisonGenerator(callLog));
            },
            CollectRunSettings([("Radar:Companies:0", "CASS")]));

        await RunComposedWorkerAsync(provider);

        var run = Assert.Single(await provider.GetRequiredService<IPipelineRunStore>()
            .ReadRecentAsync(10, default));
        Assert.Equal(["CASS"], run.CompanyFilter);
        // Still a collect pass in every other respect (spec 144).
        Assert.Null(run.Strategies);
        Assert.Equal(0, run.CompaniesScored);

        Assert.Empty(callLog);
    }

    /// <summary>
    /// The unfiltered control for the test above, over the SAME composed graph: without
    /// <c>Radar:Companies</c> the run record stamps <c>null</c> (whole universe) and the efficacy render +
    /// leaderboard run exactly as they do today. This is what makes the assertions above non-vacuous.
    /// </summary>
    [Fact]
    public async Task ComposedUnfilteredCollectRun_StampsNull_AndStillRunsEfficacy()
    {
        var callLog = new List<string>();

        using var provider = BuildProvider(
            services =>
            {
                services.AddSingleton<IEfficacyReportGenerator>(new RecordingEfficacyGenerator(callLog));
                services.AddSingleton<IStrategyComparisonReportGenerator>(
                    new RecordingStrategyComparisonGenerator(callLog));
            },
            CollectRunSettings([]));

        await RunComposedWorkerAsync(provider);

        var run = Assert.Single(await provider.GetRequiredService<IPipelineRunStore>()
            .ReadRecentAsync(10, default));
        Assert.Null(run.CompanyFilter);

        Assert.Equal(["efficacy", "comparison"], callLog);
    }

    /// <summary>
    /// Runs the COMPOSED hosted <see cref="Worker"/> — the production entry point, so seeding, the collect
    /// pipeline and the efficacy step all run through the real graph.
    /// </summary>
    private static async Task RunComposedWorkerAsync(ServiceProvider provider)
    {
        var worker = provider.GetServices<IHostedService>().OfType<Worker>().Single();
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;
    }

    /// <summary>
    /// A self-contained <c>collect</c>-mode configuration: the temp seed, the ONLY collector that issues no
    /// network request, and every output root redirected under the test's temp directory so a composed run
    /// writes nothing into the repository.
    /// </summary>
    private (string Key, string Value)[] CollectRunSettings((string Key, string Value)[] extra) =>
    [
        ("Radar:RunMode", "collect"),
        ("Radar:CompanySeedFilePath", _seedPath),
        ("Radar:Collectors:0", "localfile"),
        ("Radar:EvidenceSourceDirectory", Path.Combine(_tempDir, "evidence")),
        ("Radar:EvidenceRawDirectory", Path.Combine(_tempDir, "evidence", "raw")),
        ("Radar:SignalsDirectory", Path.Combine(_tempDir, "signals")),
        ("Radar:ScoresDirectory", Path.Combine(_tempDir, "scores")),
        ("Radar:ReportDirectory", Path.Combine(_tempDir, "reports")),
        ("Radar:RunsDirectory", Path.Combine(_tempDir, "runs")),
        ("Radar:ScoringConfigsDirectory", Path.Combine(_tempDir, "scoring-configs")),
        .. extra,
    ];

    private sealed class RecordingEfficacyGenerator(List<string> callLog) : IEfficacyReportGenerator
    {
        public Task GenerateAsync(CancellationToken ct)
        {
            callLog.Add("efficacy");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStrategyComparisonGenerator(List<string> callLog)
        : IStrategyComparisonReportGenerator
    {
        public Task<StrategyLeaderboard> GenerateAsync(CancellationToken ct)
        {
            callLog.Add("comparison");
            return Task.FromResult(new StrategyLeaderboard(
                StrategiesCompared: 0,
                StrategiesConsidered: 0,
                Rows: [],
                DroppedStrategies: [],
                Windows: new StrategyComparisonWindows(0, 0, 0, null, null, null, null),
                Options: StrategyComparisonOptions.Default));
        }
    }

    [Fact]
    public async Task UnknownTicker_FailsTheRun_RatherThanCollectingNothing()
    {
        // Composition-root startup succeeds (the seed is not known there); the failure lands at seeding
        // time, which is the first thing the worker does — before any collector issues a request.
        using var provider = BuildFilteredCollectProvider("NOSUCHTICKER");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'NOSUCHTICKER'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("holds 3 ticker(s)", ex.Message, StringComparison.Ordinal);
    }
}
