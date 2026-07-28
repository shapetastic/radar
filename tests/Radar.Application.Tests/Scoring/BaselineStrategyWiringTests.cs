using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 154 — the three shipped <c>baseline-*</c> strategies travel through the EXISTING strategy machinery
/// with no special-casing: they bind from config, they are validated by the same fail-fast rules, their
/// channel collectors are held to the same registered-collector guard, and each gets its own identity — while
/// moving nobody else's stamp.
/// <para>
/// Also pins the §E equivalence that makes it safe to write <c>Radar:Strategies</c> into the baseline run
/// profile at all: an EXPLICITLY-declared <c>{ "Name": "default", "ScoringProfile": "default" }</c> primary
/// resolves to a strategy byte-identical to the one Radar SYNTHESISES when <c>Radar:Strategies</c> is absent.
/// Without that, adding the baselines would silently re-stamp the live primary series.
/// </para>
/// <para>Modelled on <see cref="ScoringStrategyV10WiringTests"/> rather than re-inventing its harness.</para>
/// </summary>
public sealed class BaselineStrategyWiringTests
{
    /// <summary>
    /// The seven collector names <c>scripts/run-profiles/default.json</c> enables, spelled exactly as the
    /// concrete collectors report them. <c>DefaultRunProfileTests</c> checks the profile's own channel names
    /// against the Worker's kind↦name table; this list keeps THIS file honest about what it is simulating.
    /// </summary>
    private static readonly string[] BaselineCollectors =
    [
        "RssPressReleaseCollector",
        "sec-edgar",
        "usaspending",
        "newssearch",
        "sec-form4",
        "sec-13dg",
        "fda",
    ];

    private sealed class NamedCollector(string name) : IEvidenceCollector
    {
        public string CollectorName => name;
        public EvidenceSourceType SourceType => EvidenceSourceType.NewsArticle;
        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(new CollectionResult([], CollectionSummary.Empty));
    }

    private static ISignalSourceDescriptor DescriptorOver(params string[] names) =>
        new SignalSourceDescriptor(EnabledCollectorVocabulary.FromCollectors(
            names.Select(n => (IEvidenceCollector)new NamedCollector(n))));

    private static ServiceProvider BuildGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddFileSignalStore(Path.Combine(Path.GetTempPath(), $"radar-signals-{Guid.NewGuid():N}"));
        return services.BuildServiceProvider();
    }

    private static ScoringStrategyFactory FactoryOver(
        ServiceProvider provider, ScoringStrategySet set, ISignalSourceDescriptor descriptor) =>
        new(
            set,
            provider.GetRequiredService<ISignalRepository>(),
            provider.GetRequiredService<ISignalFileStore>(),
            provider.GetRequiredService<IEvidenceRepository>(),
            new StrategyScopedScoreRepositoryFactory(provider.GetRequiredService<IScoreRepository>()),
            provider.GetRequiredService<ICompanyRepository>(),
            provider.GetRequiredService<IScoreFormulaFactory>(),
            provider.GetRequiredService<IAttentionSourceWeights>(),
            descriptor,
            provider.GetRequiredService<InsiderMaterialityWeights>(),
            provider.GetRequiredService<MediaAttentionCollapse>(),
            provider.GetRequiredService<ScoringOptions>(),
            provider.GetRequiredService<ILogger<ScoringEngine>>());

    private static ScoringStrategySet Resolve(IDictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddRadarScoringStrategies(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ScoringStrategySet>();
    }

    /// <summary>
    /// The exact <c>Radar:Strategies</c> shape <c>scripts/run-profiles/default.json</c> declares, as flat
    /// configuration keys. Kept here so the identity/equivalence assertions below run against the SAME
    /// declaration an operator ships, without this Application-level test having to read a script file.
    /// </summary>
    private static Dictionary<string, string?> BaselineProfileStrategies() => new()
    {
        ["Radar:Strategies:0:Name"] = "default",
        ["Radar:Strategies:0:ScoringProfile"] = "default",

        ["Radar:Strategies:1:Name"] = "baseline-earnings-only",
        ["Radar:Strategies:1:ScoringProfile"] = "default",
        ["Radar:Strategies:1:SignalTypes:0"] = "GuidanceChange",

        ["Radar:Strategies:2:Name"] = "baseline-activity-only",
        ["Radar:Strategies:2:Formula"] = "radar-baseline-activity-v1",
        ["Radar:Strategies:2:Channels:0:Name"] = "activity",
        ["Radar:Strategies:2:Channels:0:Weight"] = "1.00",
        ["Radar:Strategies:2:Channels:0:Saturation"] = "60",
        ["Radar:Strategies:2:Channels:0:Collectors:0"] = "RssPressReleaseCollector",
        ["Radar:Strategies:2:Channels:0:Collectors:1"] = "sec-edgar",
        ["Radar:Strategies:2:Channels:0:Collectors:2"] = "usaspending",
        ["Radar:Strategies:2:Channels:0:Collectors:3"] = "newssearch",
        ["Radar:Strategies:2:Channels:0:Collectors:4"] = "sec-form4",
        ["Radar:Strategies:2:Channels:0:Collectors:5"] = "sec-13dg",
        ["Radar:Strategies:2:Channels:0:Collectors:6"] = "fda",

        ["Radar:Strategies:3:Name"] = "baseline-media-only",
        ["Radar:Strategies:3:Formula"] = "radar-baseline-activity-v1",
        ["Radar:Strategies:3:Channels:0:Name"] = "media",
        ["Radar:Strategies:3:Channels:0:Weight"] = "1.00",
        ["Radar:Strategies:3:Channels:0:Saturation"] = "20",
        ["Radar:Strategies:3:Channels:0:Collectors:0"] = "RssPressReleaseCollector",
        ["Radar:Strategies:3:Channels:0:Collectors:1"] = "newssearch",

        ["Radar:PrimaryStrategy"] = "default",
    };

    // ---------------------------------------------------------------------------------------------------
    // Config binding — the three baselines bind, validate and start
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheThreeBaselines_Bind_AndDeclareExactlyWhatTheySayTheyDo()
    {
        var set = Resolve(BaselineProfileStrategies());

        Assert.Equal(
            ["default", "baseline-earnings-only", "baseline-activity-only", "baseline-media-only"],
            set.Strategies.Select(s => s.Name));
        Assert.Equal("default", set.Primary.Name);

        // baseline-earnings-only is CONFIG-ONLY: the shipped radar-formula-v8 over a single signal type.
        var earnings = set.Strategies.Single(s => s.Name == "baseline-earnings-only");
        Assert.Equal(ScoreFormulaVersions.V8, earnings.Formula);
        Assert.True(earnings.Channels.IsEmpty);
        Assert.Equal(SignalTypeFilter.Create([SignalType.GuidanceChange]), earnings.SignalTypes);
        Assert.NotEqual(SignalTypeFilter.All, earnings.SignalTypes);

        // baseline-activity-only: ONE collector channel over every enabled collector, the whole budget.
        var activity = set.Strategies.Single(s => s.Name == "baseline-activity-only");
        Assert.Equal(ScoreFormulaVersions.BaselineActivityV1, activity.Formula);
        var activityChannel = Assert.Single(activity.Channels.Channels);
        Assert.Equal(ScoringChannelKind.Collector, activityChannel.Kind);
        Assert.Equal(1.0, activityChannel.Weight);
        Assert.Equal(60.0, activityChannel.Saturation);
        Assert.Equal(BaselineCollectors.Order(StringComparer.Ordinal), activityChannel.Collectors);

        // baseline-media-only: the same formula over the press/news collectors ONLY.
        var media = set.Strategies.Single(s => s.Name == "baseline-media-only");
        Assert.Equal(ScoreFormulaVersions.BaselineActivityV1, media.Formula);
        var mediaChannel = Assert.Single(media.Channels.Channels);
        Assert.Equal(1.0, mediaChannel.Weight);
        Assert.Equal(20.0, mediaChannel.Saturation);
        Assert.Equal(["RssPressReleaseCollector", "newssearch"], mediaChannel.Collectors);
    }

    [Fact]
    public void TheThreeBaselines_Start_WithEveryChannelCollectorResolvingAgainstTheRegisteredSet()
    {
        // THE TYPO CATCHER. ScoringStrategyFactory validates every declared channel collector against the
        // enabled-collector vocabulary — exactly, ordinally — before any engine is built. A misspelled or
        // mis-cased collector name in the shipped profile would silently cost that channel its whole share
        // forever; here it is a startup failure instead.
        using var provider = BuildGraph();

        var runtimes = FactoryOver(
            provider, Resolve(BaselineProfileStrategies()), DescriptorOver(BaselineCollectors)).Runtimes;

        Assert.Equal(4, runtimes.Count);
        Assert.All(runtimes, r => Assert.NotNull(r.Engine.EffectiveConfig));
    }

    [Fact]
    public void AMisspelledCollectorInABaselineChannel_FailsFastBeforeAnyEngineIsBuilt()
    {
        // The positive control for the test above: the guard is genuinely load-bearing here, not vacuous.
        var config = BaselineProfileStrategies();
        config["Radar:Strategies:3:Channels:0:Collectors:1"] = "NewsSearch"; // wrong casing

        using var provider = BuildGraph();
        var factory = FactoryOver(provider, Resolve(config), DescriptorOver(BaselineCollectors));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Runtimes);

        Assert.Contains("baseline-media-only", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NewsSearch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABaselineStrategyDeclaringABreadthChannel_FailsFastNamingTheReason()
    {
        // The control formula rejects a breadth channel (its reach is TIER-WEIGHTED, i.e. a quality
        // weighting). That refusal has to surface at STARTUP, through the normal factory path, not at the
        // first scoring call — so it is asserted here as well as on the formula itself.
        var config = BaselineProfileStrategies();
        config["Radar:Strategies:3:Channels:0:Weight"] = "0.50";
        config["Radar:Strategies:3:Channels:1:Name"] = "attention";
        config["Radar:Strategies:3:Channels:1:Kind"] = "breadth";
        config["Radar:Strategies:3:Channels:1:Weight"] = "0.50";
        config["Radar:Strategies:3:Channels:1:Saturation"] = "3";

        using var provider = BuildGraph();
        var factory = FactoryOver(provider, Resolve(config), DescriptorOver(BaselineCollectors));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Runtimes);

        Assert.Contains("attention", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tier-weighted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------------
    // Identity — a baseline is its own series, and adding one moves nobody
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EachBaseline_StampsItsOwnIdentity_AndAddingThemMovesNoExistingStamp()
    {
        // Two guarantees, one test, because they are the same one seen from both sides: a baseline can never
        // be pooled into another strategy's series, and introducing the control group cannot re-stamp the
        // series it is meant to be compared against (which would break every accrued snapshot's continuity).
        using var provider = BuildGraph();
        var descriptor = DescriptorOver(BaselineCollectors);

        var before = FactoryOver(
                provider,
                Resolve(new Dictionary<string, string?>
                {
                    ["Radar:Strategies:0:Name"] = "default",
                    ["Radar:Strategies:0:ScoringProfile"] = "default",
                    ["Radar:PrimaryStrategy"] = "default",
                }),
                descriptor)
            .Runtimes;

        var after = FactoryOver(provider, Resolve(BaselineProfileStrategies()), descriptor).Runtimes;

        // The pre-existing strategy's whole effective config — weights, descriptor, fingerprint — is unmoved.
        Assert.Equal(before[0].Engine.EffectiveConfig, after[0].Engine.EffectiveConfig);

        var fingerprints = after.Select(r => r.Engine.EffectiveConfig.Fingerprint).ToArray();
        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());

        // The two baselines that share a FORMULA still differ, because their channel budgets do — a budget is
        // a fingerprint input (spec 146).
        Assert.NotEqual(fingerprints[2], fingerprints[3]);

        // The composed formula identity carries the composition revision, so a baseline's stamp is legible
        // rather than opaque.
        Assert.Equal(ScoreFormulaVersions.V8, after[0].Engine.EffectiveConfig.FormulaVersion);
        Assert.Equal(ScoreFormulaVersions.V8, after[1].Engine.EffectiveConfig.FormulaVersion);
        Assert.Equal(
            $"{ScoreFormulaVersions.BaselineActivityV1}@rev1", after[2].Engine.EffectiveConfig.FormulaVersion);
        Assert.Equal(
            $"{ScoreFormulaVersions.BaselineActivityV1}@rev1", after[3].Engine.EffectiveConfig.FormulaVersion);
    }

    // ---------------------------------------------------------------------------------------------------
    // §E — the explicit `default` is the synthesised `default`
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AnExplicitDefaultPrimary_IsIdenticalToTheSynthesisedOne_DefinitionAndStamp()
    {
        // THE PIN THAT MAKES SHIPPING Radar:Strategies SAFE. Before the baseline run profile declared
        // Radar:Strategies at all, the live primary was the SYNTHESISED "default" strategy. Writing the
        // list out explicitly must reproduce that strategy exactly — same profile, same weights, same filter,
        // same formula, same (absent) budget and, above all, the same ScoringConfigVersion. If it did not, the
        // live series would silently split in two and StrategyIdentityGuard would trip on the primary.
        var synthesised = Resolve(new Dictionary<string, string?>()).Strategies.Single();
        var explicitly = Resolve(BaselineProfileStrategies()).Strategies.Single(s => s.Name == "default");

        Assert.Equal(synthesised.Name, explicitly.Name);
        Assert.Equal(synthesised.ScoringProfile, explicitly.ScoringProfile);
        Assert.Equal(synthesised.IsPrimary, explicitly.IsPrimary);
        Assert.True(explicitly.IsPrimary);
        Assert.Equal(synthesised.Weights, explicitly.Weights);
        Assert.Equal(synthesised.SignalTypes, explicitly.SignalTypes);
        Assert.Equal(SignalTypeFilter.All, explicitly.SignalTypes);
        Assert.Equal(synthesised.Formula, explicitly.Formula);
        Assert.Equal(ScoreFormulaVersions.V8, explicitly.Formula);
        Assert.Equal(synthesised.Channels, explicitly.Channels);
        Assert.True(explicitly.Channels.IsEmpty);

        // …and the stamp, resolved through the real factory over the same graph and the same descriptor.
        using var provider = BuildGraph();
        var descriptor = DescriptorOver(BaselineCollectors);

        var synthesisedConfig = FactoryOver(
            provider, Resolve(new Dictionary<string, string?>()), descriptor).Primary.Engine.EffectiveConfig;
        var explicitConfig = FactoryOver(
            provider, Resolve(BaselineProfileStrategies()), descriptor).Primary.Engine.EffectiveConfig;

        Assert.Equal(synthesisedConfig, explicitConfig);
        Assert.Equal(synthesisedConfig.Fingerprint, explicitConfig.Fingerprint);
    }
}
