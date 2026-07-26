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
/// Spec 146 — the formula and the channel budget as PER-STRATEGY identity: they reach the strategy's engine,
/// they re-stamp only that strategy's <c>ScoringConfigVersion</c>, and a strategy that declares neither is
/// byte-identical to before the slice (the pinned fingerprints do not move).
/// </summary>
public sealed class ScoringStrategyFormulaChannelTests
{
    private sealed class NamedCollector(string name) : IEvidenceCollector
    {
        public string CollectorName => name;
        public EvidenceSourceType SourceType => EvidenceSourceType.NewsArticle;
        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(new CollectionResult([], CollectionSummary.Empty));
    }

    private static ISignalSourceDescriptor DescriptorOver(params string[] names) =>
        new SignalSourceDescriptor(names.Select(n => (IEvidenceCollector)new NamedCollector(n)));

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

    // ---------------------------------------------------------------------------------------------------
    // Defaults are byte-identical
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void OmittingFormula_KeepsTheStrategyOnV8_WithNoChannels()
    {
        var definition = new ScoringStrategyDefinition("baseline", "default", new ScoringWeights(), true);

        Assert.Equal(ScoreFormulaVersions.V8, definition.Formula);
        Assert.Same(ScoringChannelSet.Empty, definition.Channels);

        using var provider = BuildGraph();
        var formula = provider.GetRequiredService<IScoreFormulaFactory>().Create(definition);

        Assert.IsType<RadarScoreFormulaV8>(formula);
        Assert.Equal(ScoreFormulaVersions.V8, formula.Version);
    }

    [Fact]
    public void OmittingFormula_StampsExactlyWhatTheSingleEngineCompositionStamps()
    {
        // The byte-identical guarantee, at the level that matters: the default strategy's stamp is unmoved,
        // which is what keeps the pinned AI-OFF/AI-ON fingerprints (asserted in ScoringConfigFingerprintTests)
        // valid after this slice.
        using var provider = BuildGraph();
        var descriptor = provider.GetRequiredService<ISignalSourceDescriptor>();
        var weights = provider.GetRequiredService<ScoringWeights>();

        var factory = FactoryOver(
            provider,
            ScoringStrategySet.SingleDefault(weights),
            descriptor);

        Assert.Equal(
            descriptor.CanonicalDescriptor(),
            factory.Primary.Engine.EffectiveConfig.SignalSourceDescriptor);
        Assert.Equal(ScoreFormulaVersions.V8, factory.Primary.Engine.EffectiveConfig.FormulaVersion);
    }

    // ---------------------------------------------------------------------------------------------------
    // A v9 strategy is a different identity, and it moves nobody else's
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AddingAV9Strategy_MovesNoExistingStrategysScoringConfigVersion()
    {
        // The additive guarantee: introducing a channel strategy alongside an existing one must not re-stamp
        // the existing one, or every accrued snapshot of the existing series would stop matching new ones.
        using var provider = BuildGraph();
        var descriptor = DescriptorOver("patents", "sec-form4");
        var weights = new ScoringWeights();

        var before = FactoryOver(
                provider,
                new ScoringStrategySet(
                    [new ScoringStrategyDefinition("baseline", "default", weights, IsPrimary: true)]),
                descriptor)
            .Primary.Engine.EffectiveConfig;

        var after = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("baseline", "default", weights, IsPrimary: true),
                new ScoringStrategyDefinition("patents-led", "default", weights, IsPrimary: false)
                {
                    Formula = ScoreFormulaVersions.V9,
                    Channels = Budget("patents-led", "patents"),
                },
            ]),
            descriptor);

        var baselineAfter = after.Runtimes[0].Engine.EffectiveConfig;
        var v9 = after.Runtimes[1].Engine.EffectiveConfig;

        Assert.Equal(before.Fingerprint, baselineAfter.Fingerprint);
        Assert.Equal(before, baselineAfter);
        // …and the v9 strategy is genuinely a different identity (different formula AND different budget).
        Assert.NotEqual(before.Fingerprint, v9.Fingerprint);
        Assert.Equal(ScoreFormulaVersions.V9, v9.FormulaVersion);
    }

    [Fact]
    public void TwoV9Strategies_DifferingOnlyInChannelWeights_StampDifferently()
    {
        // The channel budget IS identity: two strategies allocating their score differently are genuinely
        // different scorings and must never share a ScoringConfigVersion.
        using var provider = BuildGraph();
        var descriptor = DescriptorOver("patents", "sec-form4");
        var weights = new ScoringWeights();

        var factory = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("a", "default", weights, IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V9,
                    Channels = ScoringChannelSet.Create(
                        [
                            ScoringChannel.Collector("sources", ["patents"], 0.5, 3),
                            ScoringChannel.Breadth("attention", 0.5, 3),
                        ],
                        "a"),
                },
                new ScoringStrategyDefinition("b", "default", weights, IsPrimary: false)
                {
                    Formula = ScoreFormulaVersions.V9,
                    Channels = ScoringChannelSet.Create(
                        [
                            ScoringChannel.Collector("sources", ["patents"], 0.9, 3),
                            ScoringChannel.Breadth("attention", 0.1, 3),
                        ],
                        "b"),
                },
            ]),
            descriptor);

        Assert.NotEqual(
            factory.Runtimes[0].Engine.EffectiveConfig.Fingerprint,
            factory.Runtimes[1].Engine.EffectiveConfig.Fingerprint);
        Assert.EndsWith(
            "channels=attention:breadth:0.5:3:,sources:collector:0.5:3:patents;",
            factory.Runtimes[0].Engine.EffectiveConfig.SignalSourceDescriptor,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheChannelSegment_IsAppendedAfterTheSignalTypeSegment()
    {
        // Fixed field ordering (AD-3): both optional segments fold onto the SAME Describe chain, in a stable
        // order, so a strategy declaring both cannot hash ambiguously.
        using var provider = BuildGraph();
        var descriptor = DescriptorOver("patents");
        var shared = descriptor.CanonicalDescriptor();

        var factory = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("both", "default", new ScoringWeights(), IsPrimary: true)
                {
                    SignalTypes = SignalTypeFilter.Create([Radar.Domain.Signals.SignalType.PatentActivity]),
                    Formula = ScoreFormulaVersions.V9,
                    Channels = ScoringChannelSet.Create(
                        [ScoringChannel.Collector("sources", ["patents"], 1.0, 3)], "both"),
                },
            ]),
            descriptor);

        Assert.Equal(
            $"{shared}signalTypes=PatentActivity;channels=sources:collector:1:3:patents;",
            factory.Primary.Engine.EffectiveConfig.SignalSourceDescriptor);
    }

    // ---------------------------------------------------------------------------------------------------
    // Fail-fast validation
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownFormula_FailsFast_NamingTheStrategyAndTheKnownFormulas()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("typo", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = "radar-formula-v42",
                },
            ]));

        Assert.Contains("typo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("radar-formula-v42", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.V8, ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.V9, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V9WithoutChannels_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("empty", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V9,
                },
            ]));

        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no Channels", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelsWithoutV9_FailFast_RatherThanBeingSilentlyIgnored()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("confused", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Channels = Budget("confused", "patents"),
                },
            ]));

        Assert.Contains("confused", ex.Message, StringComparison.Ordinal);
        Assert.Contains("silently ignored", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullChannels_FailFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("nulled", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Channels = null!,
                },
            ]));

        Assert.Contains("null Channels", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelNamingAnUnregisteredCollector_FailsFast_BeforeAnyEngineIsBuilt()
    {
        // Validated where the registry is actually known, and forced while the runtimes are built — which
        // StrategyIdentityGuard does as the very first statement of RunAsync, so the typo costs no collection.
        using var provider = BuildGraph();

        var factory = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("typo", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V9,
                    Channels = Budget("typo", "pattents"),
                },
            ]),
            DescriptorOver("patents", "sec-form4"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Runtimes);

        Assert.Contains("typo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pattents", ex.Message, StringComparison.Ordinal);
        Assert.Contains("patents, sec-form4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelNamingARegisteredCollector_WithTheWrongCasing_AlsoFailsFast()
    {
        // Collector names are matched EXACTLY: a near-miss that quietly selects nothing is precisely the
        // silent failure a declared budget exists to prevent.
        using var provider = BuildGraph();

        var factory = FactoryOver(
            provider,
            new ScoringStrategySet(
            [
                new ScoringStrategyDefinition("casing", "default", new ScoringWeights(), IsPrimary: true)
                {
                    Formula = ScoreFormulaVersions.V9,
                    Channels = Budget("casing", "Patents"),
                },
            ]),
            DescriptorOver("patents"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Runtimes);
        Assert.Contains("Patents", ex.Message, StringComparison.Ordinal);
        Assert.Contains("casing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredCollectorNames_AreTheSameProjectionAsTheRecordedCollectionProvenance()
    {
        // One projection, two renderings (spec 141 + 146): "what the snapshot says was collected" and "what a
        // v9 channel says ran" must never be able to disagree.
        var descriptor = DescriptorOver("sec-form4", "patents", "patents");

        Assert.Equal(["patents", "sec-form4"], descriptor.EnabledCollectors());
        Assert.Equal("collectors=patents,sec-form4;", descriptor.CollectionProvenance());
    }

    [Fact]
    public void V9Strategy_GetsAV9Formula_BoundToItsOwnChannels()
    {
        using var provider = BuildGraph();
        var definition = new ScoringStrategyDefinition(
            "patents-led", "default", new ScoringWeights(), IsPrimary: true)
        {
            Formula = ScoreFormulaVersions.V9,
            Channels = Budget("patents-led", "patents"),
        };

        var formula = Assert.IsType<RadarScoreFormulaV9>(
            provider.GetRequiredService<IScoreFormulaFactory>().Create(definition));

        Assert.Equal(definition.Channels, formula.Channels);
    }
}
