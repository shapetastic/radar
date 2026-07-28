using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Domain.Evidence;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 153 — <c>radar-formula-v10</c> travels through the EXISTING strategy machinery exactly as
/// <c>radar-formula-v9</c> does: it binds from config, it is validated by the same fail-fast rules
/// (generalised from a hard-coded v9 onto <see cref="ScoreFormulaVersions.ConsumesChannels"/>), the
/// registered-collector guard applies to it unchanged, and it is a DISTINCT identity that moves nobody else's
/// stamp.
/// <para>
/// Modelled on <see cref="ScoringStrategyFormulaChannelTests"/> and
/// <see cref="ScoringChannelBindingTests"/> rather than re-inventing their harnesses.
/// </para>
/// </summary>
public sealed class ScoringStrategyV10WiringTests
{
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

    private static ScoringChannelSet Budget(string strategyName, params string[] collectors) =>
        ScoringChannelSet.Create(
            [
                ScoringChannel.Collector("sources", collectors, 0.7, 3),
                ScoringChannel.Breadth("attention", 0.3, 3),
            ],
            strategyName);

    private static ScoringStrategySet Resolve(IDictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddRadarScoringStrategies(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ScoringStrategySet>();
    }

    // ---------------------------------------------------------------------------------------------------
    // The formula factory
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void V10Strategy_GetsAV10Formula_BoundToItsOwnChannels()
    {
        using var provider = BuildGraph();
        var definition = new ScoringStrategyDefinition(
            "directional", "default", new ScoringWeights(), IsPrimary: true)
        {
            Formula = ScoreFormulaVersions.V10,
            Channels = Budget("directional", "patents"),
        };

        var formula = Assert.IsType<RadarScoreFormulaV10>(
            provider.GetRequiredService<IScoreFormulaFactory>().Create(definition));

        Assert.Equal(definition.Channels, formula.Channels);
        Assert.Equal(ScoreFormulaVersions.V10, formula.Version);
    }

    [Fact]
    public void ShippableFormulas_IncludeV10_InVersionOrder_AndOnlyTheChannelOnesConsumeChannels()
    {
        Assert.Equal(
            [ScoreFormulaVersions.V8, ScoreFormulaVersions.V9, ScoreFormulaVersions.V10],
            ScoreFormulaVersions.All);

        Assert.False(ScoreFormulaVersions.ConsumesChannels(ScoreFormulaVersions.V8));
        Assert.True(ScoreFormulaVersions.ConsumesChannels(ScoreFormulaVersions.V9));
        Assert.True(ScoreFormulaVersions.ConsumesChannels(ScoreFormulaVersions.V10));
        Assert.False(ScoreFormulaVersions.ConsumesChannels("radar-formula-v42"));
        Assert.False(ScoreFormulaVersions.ConsumesChannels(null));

        // The fail-fast message list is rendered FROM All through the same predicate the rules use, so a
        // message can never name a different set from the one enforced.
        Assert.Equal(
            $"{ScoreFormulaVersions.V9}, {ScoreFormulaVersions.V10}", ScoreFormulaVersions.ChannelFormulaList);
    }

    // ---------------------------------------------------------------------------------------------------
    // Validation, generalised off the hard-coded V9
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void V10WithoutChannels_FailsFast_ExactlyAsV9Does()
    {
        var v10 = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("empty-v10", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V10,
                },
            ]));

        Assert.Contains("empty-v10", v10.Message, StringComparison.Ordinal);
        Assert.Contains("no Channels", v10.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.V10, v10.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelsWithoutAChannelFormula_StillFailFast_AndTheMessageNamesEveryChannelFormula()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("confused", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Channels = Budget("confused", "patents"),
                },
            ]));

        Assert.Contains("silently ignored", ex.Message, StringComparison.Ordinal);
        // Generalised by spec 153: the remedy names the SET of channel formulas, not just v9.
        Assert.Contains(ScoreFormulaVersions.V9, ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.V10, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFormula_FailsFast_NamingV10AmongTheKnownFormulas()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("typo", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = "radar-formula-v42",
                },
            ]));

        Assert.Contains(ScoreFormulaVersions.V10, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V10ChannelNamingAnUnregisteredCollector_FailsFast_BeforeAnyEngineIsBuilt()
    {
        // ScoringStrategyFactory's guard keys off the declared Channels, not off the formula, so it is
        // already formula-agnostic — confirmed here rather than assumed.
        using var provider = BuildGraph();

        var factory = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("typo", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V10,
                    Channels = Budget("typo", "pattents"),
                },
            ]),
            DescriptorOver("patents", "sec-form4"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Runtimes);

        Assert.Contains("typo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pattents", ex.Message, StringComparison.Ordinal);
        Assert.Contains("patents, sec-form4", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    // Identity
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AV10Strategy_StampsDifferentlyFromAnOtherwiseIdenticalV9_AndMovesNoExistingStamp()
    {
        // Two things at once, because they are the same guarantee seen from both sides: v10 is genuinely its
        // own identity (so its snapshots can never be pooled with a v9 series), and introducing it re-stamps
        // nobody — otherwise every accrued snapshot of an existing series would stop matching new ones.
        using var provider = BuildGraph();
        var descriptor = DescriptorOver("patents", "sec-form4");
        var weights = new ScoringWeights();

        var before = FactoryOver(
                provider,
                new ScoringStrategySet(
                [
                    new ScoringStrategyDefinition("baseline", "default", weights, IsPrimary: true),
                    new ScoringStrategyDefinition("channels-v9", "default", weights, IsPrimary: false)
                    {
                        Formula = ScoreFormulaVersions.V9,
                        Channels = Budget("channels-v9", "patents"),
                    },
                ]),
                descriptor)
            .Runtimes;

        var after = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("baseline", "default", weights, IsPrimary: true),
                new ScoringStrategyDefinition("channels-v9", "default", weights, IsPrimary: false)
                {
                    Formula = ScoreFormulaVersions.V9,
                    Channels = Budget("channels-v9", "patents"),
                },
                // Identical in EVERY respect to channels-v9 except the formula.
                new ScoringStrategyDefinition("channels-v10", "default", weights, IsPrimary: false)
                {
                    Formula = ScoreFormulaVersions.V10,
                    Channels = Budget("channels-v10", "patents"),
                },
            ]),
            descriptor).Runtimes;

        Assert.Equal(before[0].Engine.EffectiveConfig, after[0].Engine.EffectiveConfig);
        Assert.Equal(before[1].Engine.EffectiveConfig, after[1].Engine.EffectiveConfig);

        var v9 = after[1].Engine.EffectiveConfig;
        var v10 = after[2].Engine.EffectiveConfig;

        Assert.NotEqual(v9.Fingerprint, v10.Fingerprint);
        Assert.NotEqual(before[0].Engine.EffectiveConfig.Fingerprint, v10.Fingerprint);

        // The composed identity (spec 153) is what is persisted and hashed — v9 declares no revision, v10
        // does, and the difference is visible rather than buried in the hash.
        Assert.Equal(ScoreFormulaVersions.V9, v9.FormulaVersion);
        Assert.Equal($"{ScoreFormulaVersions.V10}@rev1", v10.FormulaVersion);
    }

    // ---------------------------------------------------------------------------------------------------
    // Config binding
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AV10StrategyBindsValidatesAndStartsFromConfig_ExactlyLikeAV9One()
    {
        var only = Assert.Single(Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "directional",
            ["Radar:Strategies:0:Formula"] = "radar-formula-v10",
            ["Radar:Strategies:0:Channels:0:Name"] = "insider",
            ["Radar:Strategies:0:Channels:0:Collectors:0"] = "sec-form4",
            ["Radar:Strategies:0:Channels:0:Weight"] = "0.70",
            ["Radar:Strategies:0:Channels:0:Saturation"] = "2",
            ["Radar:Strategies:0:Channels:1:Name"] = "attention",
            ["Radar:Strategies:0:Channels:1:Kind"] = "breadth",
            ["Radar:Strategies:0:Channels:1:Weight"] = "0.30",
            ["Radar:Strategies:0:Channels:1:Saturation"] = "3",
            ["Radar:PrimaryStrategy"] = "directional",
        }).Strategies);

        Assert.Equal(ScoreFormulaVersions.V10, only.Formula);
        Assert.Equal(["attention", "insider"], only.Channels.Channels.Select(c => c.Name));

        var insider = only.Channels.Channels.Single(c => c.Name == "insider");
        Assert.Equal(ScoringChannelKind.Collector, insider.Kind);
        Assert.Equal(["sec-form4"], insider.Collectors);
        Assert.Equal(0.70, insider.Weight);
        Assert.Equal(2.0, insider.Saturation);
    }

    [Fact]
    public void V10FormulaName_IsMatchedCaseInsensitively_AndCanonicalised()
    {
        var only = Assert.Single(Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "directional",
            ["Radar:Strategies:0:Formula"] = "  RADAR-Formula-V10 ",
            ["Radar:Strategies:0:Channels:0:Name"] = "insider",
            ["Radar:Strategies:0:Channels:0:Collectors:0"] = "sec-form4",
            ["Radar:Strategies:0:Channels:0:Weight"] = "1.00",
            ["Radar:Strategies:0:Channels:0:Saturation"] = "2",
            ["Radar:PrimaryStrategy"] = "directional",
        }).Strategies);

        Assert.Equal(ScoreFormulaVersions.V10, only.Formula);
    }

    [Fact]
    public void AV10StrategyDeclaringNoChannels_FailsFastAtTheConfigBoundary()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Radar:Strategies:0:Name"] = "directional",
                ["Radar:Strategies:0:Formula"] = "radar-formula-v10",
                ["Radar:PrimaryStrategy"] = "directional",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringStrategies(configuration));

        Assert.Contains("directional", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no Channels", ex.Message, StringComparison.Ordinal);
    }
}
