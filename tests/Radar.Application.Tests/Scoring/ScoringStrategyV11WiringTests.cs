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
/// Spec 157 — <c>radar-formula-v11</c> travels through the EXISTING strategy machinery exactly as v10 does
/// (binds from config, validated by the same generalised rules, distinct identity that moves nobody's stamp),
/// PLUS the one rule that is new with it: <b>a v11 strategy declaring a breadth channel fails startup</b>,
/// with a message citing spec 158's measurement and pointing at
/// <c>docs/158-channel-feasibility-findings.md</c> — fail-fast, never fail-silent, because a legal-but-dead
/// breadth budget is exactly the silently-lost-weight failure the amendment exists to prevent.
/// <para>Modelled on <see cref="ScoringStrategyV10WiringTests"/> rather than re-inventing its harness.</para>
/// </summary>
public sealed class ScoringStrategyV11WiringTests
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

    /// <summary>The predeclared spec-157 §7 budget shape: ONE collector channel, no breadth.</summary>
    private static ScoringChannelSet DisclosureBudget(string strategyName) =>
        ScoringChannelSet.Create(
            [ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3)], strategyName);

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
    public void V11Strategy_GetsAV11Formula_BoundToItsOwnChannels()
    {
        using var provider = BuildGraph();
        var definition = new ScoringStrategyDefinition(
            "disclosure", "default", new ScoringWeights(), IsPrimary: true)
        {
            Formula = ScoreFormulaVersions.V11,
            Channels = DisclosureBudget("disclosure"),
        };

        var formula = Assert.IsType<RadarScoreFormulaV11>(
            provider.GetRequiredService<IScoreFormulaFactory>().Create(definition));

        Assert.Equal(definition.Channels, formula.Channels);
        Assert.Equal(ScoreFormulaVersions.V11, formula.Version);
    }

    // ---------------------------------------------------------------------------------------------------
    // THE NEW RULE: breadth is rejected at startup, at the config boundary, naming the strategy
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AV11StrategyDeclaringABreadthChannel_FailsFast_CitingSpec158()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition(
                    "disclosure-plus-breadth", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V11,
                    Channels = ScoringChannelSet.Create(
                        [
                            ScoringChannel.Collector("filings", ["sec-edgar"], 0.8, 3),
                            ScoringChannel.Breadth("attention", 0.2, 3),
                        ],
                        "disclosure-plus-breadth"),
                },
            ]));

        // Names the strategy AND the offending channel, cites the finding, points at where it is recorded,
        // and offers the two legal ways out.
        Assert.Contains("disclosure-plus-breadth", ex.Message, StringComparison.Ordinal);
        Assert.Contains("attention", ex.Message, StringComparison.Ordinal);
        Assert.Contains("spec 158", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            "docs/158-channel-feasibility-findings.md", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.V10, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AV11StrategyDeclaringABreadthChannel_FailsFast_FromConfigToo()
    {
        // The same rule through the REAL binding path (AddRadarScoringStrategies), so a run-profile typo
        // fails at startup before any collection — not only a code-composed definition.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Radar:Strategies:0:Name"] = "disclosure",
                ["Radar:Strategies:0:Formula"] = "radar-formula-v11",
                ["Radar:Strategies:0:Channels:0:Name"] = "filings",
                ["Radar:Strategies:0:Channels:0:Collectors:0"] = "sec-edgar",
                ["Radar:Strategies:0:Channels:0:Weight"] = "0.80",
                ["Radar:Strategies:0:Channels:0:Saturation"] = "3",
                ["Radar:Strategies:0:Channels:1:Name"] = "attention",
                ["Radar:Strategies:0:Channels:1:Kind"] = "breadth",
                ["Radar:Strategies:0:Channels:1:Weight"] = "0.20",
                ["Radar:Strategies:0:Channels:1:Saturation"] = "3",
                ["Radar:PrimaryStrategy"] = "disclosure",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringStrategies(configuration));

        Assert.Contains("disclosure", ex.Message, StringComparison.Ordinal);
        Assert.Contains("spec 158", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            "docs/158-channel-feasibility-findings.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V9AndV10_StillAcceptBreadthChannels_TheRuleIsV11Only()
    {
        // The rejection must not leak onto the formulas that legitimately budget breadth — five live arms
        // declare one. Asserted through the same constructor path the new rule guards.
        foreach (var formula in new[] { ScoreFormulaVersions.V9, ScoreFormulaVersions.V10 })
        {
            var set = new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("with-breadth", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = formula,
                    Channels = ScoringChannelSet.Create(
                        [
                            ScoringChannel.Collector("filings", ["sec-edgar"], 0.8, 3),
                            ScoringChannel.Breadth("attention", 0.2, 3),
                        ],
                        "with-breadth"),
                },
            ]);

            Assert.Equal(formula, set.Primary.Formula);
        }

        Assert.True(ScoreFormulaVersions.RejectsBreadthChannels(ScoreFormulaVersions.V11));
        Assert.True(ScoreFormulaVersions.RejectsBreadthChannels("  RADAR-Formula-V11 "));
        Assert.False(ScoreFormulaVersions.RejectsBreadthChannels(ScoreFormulaVersions.V9));
        Assert.False(ScoreFormulaVersions.RejectsBreadthChannels(ScoreFormulaVersions.V10));
        Assert.False(ScoreFormulaVersions.RejectsBreadthChannels(null));
    }

    // ---------------------------------------------------------------------------------------------------
    // Validation inherited from the generalised channel rules
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void V11WithoutChannels_FailsFast_ExactlyAsV10Does()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("empty-v11", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V11,
                },
            ]));

        Assert.Contains("empty-v11", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no Channels", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.V11, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    // Identity
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AV11Strategy_StampsDifferentlyFromAnIdenticallyBudgetedV10_AndMovesNoExistingStamp()
    {
        // The live pair, in miniature: disclosure-led-v11 and disclosure-led-v10-control share ONE budget and
        // differ only in the formula. They must be distinct identities (their series may never pool), and
        // introducing the v11 arm must re-stamp nobody — otherwise adding it would fork every accrued series.
        using var provider = BuildGraph();
        var descriptor = DescriptorOver("sec-edgar", "sec-form4");
        var weights = new ScoringWeights();

        var before = FactoryOver(
                provider,
                new ScoringStrategySet(
                [
                    new ScoringStrategyDefinition("baseline", "default", weights, IsPrimary: true),
                    new ScoringStrategyDefinition("control-v10", "default", weights, IsPrimary: false)
                    {
                        Formula = ScoreFormulaVersions.V10,
                        Channels = DisclosureBudget("control-v10"),
                    },
                ]),
                descriptor)
            .Runtimes;

        var after = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("baseline", "default", weights, IsPrimary: true),
                new ScoringStrategyDefinition("control-v10", "default", weights, IsPrimary: false)
                {
                    Formula = ScoreFormulaVersions.V10,
                    Channels = DisclosureBudget("control-v10"),
                },
                // Identical in EVERY respect to control-v10 except the formula.
                new ScoringStrategyDefinition("disclosure-v11", "default", weights, IsPrimary: false)
                {
                    Formula = ScoreFormulaVersions.V11,
                    Channels = DisclosureBudget("disclosure-v11"),
                },
            ]),
            descriptor).Runtimes;

        Assert.Equal(before[0].Engine.EffectiveConfig, after[0].Engine.EffectiveConfig);
        Assert.Equal(before[1].Engine.EffectiveConfig, after[1].Engine.EffectiveConfig);

        var v10 = after[1].Engine.EffectiveConfig;
        var v11 = after[2].Engine.EffectiveConfig;

        Assert.NotEqual(v10.Fingerprint, v11.Fingerprint);
        Assert.NotEqual(before[0].Engine.EffectiveConfig.Fingerprint, v11.Fingerprint);

        // Both carry their own composed identity (spec 153's mechanism, which v11 must also carry).
        Assert.Equal($"{ScoreFormulaVersions.V10}@rev1", v10.FormulaVersion);
        Assert.Equal($"{ScoreFormulaVersions.V11}@rev1", v11.FormulaVersion);
    }

    // ---------------------------------------------------------------------------------------------------
    // Config binding
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AV11StrategyBindsValidatesAndStartsFromConfig_ExactlyLikeAV10One()
    {
        var only = Assert.Single(Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "disclosure",
            ["Radar:Strategies:0:Formula"] = "radar-formula-v11",
            ["Radar:Strategies:0:Channels:0:Name"] = "filings",
            ["Radar:Strategies:0:Channels:0:Collectors:0"] = "sec-edgar",
            ["Radar:Strategies:0:Channels:0:Weight"] = "1.00",
            ["Radar:Strategies:0:Channels:0:Saturation"] = "3",
            ["Radar:PrimaryStrategy"] = "disclosure",
        }).Strategies);

        Assert.Equal(ScoreFormulaVersions.V11, only.Formula);
        var channel = Assert.Single(only.Channels.Channels);
        Assert.Equal("filings", channel.Name);
        Assert.Equal(ScoringChannelKind.Collector, channel.Kind);
        Assert.Equal(["sec-edgar"], channel.Collectors);
        Assert.Equal(1.0, channel.Weight);
        Assert.Equal(3.0, channel.Saturation);
    }
}
