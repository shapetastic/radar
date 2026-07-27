using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Signals;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 137 — one <see cref="ScoringEngine"/> per strategy, built by <see cref="ScoringStrategyFactory"/>.
/// The two load-bearing properties: (a) the synthesised default strategy's engine stamps EXACTLY the
/// fingerprint the previous single-engine composition stamped, and (b) the strategy NAME is not a
/// fingerprint input.
/// </summary>
public sealed class ScoringStrategyFactoryTests
{
    private static ServiceProvider BuildDefaultGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        // The engine depends on ISignalFileStore (cross-run previous-window read); wire the real file store
        // over a unique temp dir so the composition resolves.
        services.AddFileSignalStore(Path.Combine(Path.GetTempPath(), $"radar-signals-{Guid.NewGuid():N}"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DefaultGraph_ResolvesExactlyOnePrimaryStrategy_NamedDefault()
    {
        using var provider = BuildDefaultGraph();

        var factory = provider.GetRequiredService<IScoringStrategyFactory>();

        var only = Assert.Single(factory.Runtimes);
        Assert.Equal(ScoringStrategySet.DefaultStrategyName, only.Definition.Name);
        Assert.True(only.Definition.IsPrimary);
        Assert.Same(only, factory.Primary);
    }

    [Fact]
    public void SynthesisedDefaultStrategy_StampsTheSameFingerprint_AsTheSingleEngineComposition()
    {
        using var provider = BuildDefaultGraph();

        // The engine exactly as the pre-spec-137 composition built it: the registered ScoringWeights, a
        // formula constructed straight over them, and no strategy name.
        var weights = provider.GetRequiredService<ScoringWeights>();
        var sourceWeights = provider.GetRequiredService<IAttentionSourceWeights>();
        var singleEngine = new ScoringEngine(
            provider.GetRequiredService<ISignalRepository>(),
            provider.GetRequiredService<ISignalFileStore>(),
            provider.GetRequiredService<IEvidenceRepository>(),
            provider.GetRequiredService<IScoreRepository>(),
            provider.GetRequiredService<ICompanyRepository>(),
            new RadarScoreFormulaV8(weights, sourceWeights),
            weights,
            sourceWeights,
            provider.GetRequiredService<ISignalSourceDescriptor>(),
            provider.GetRequiredService<InsiderMaterialityWeights>(),
            provider.GetRequiredService<MediaAttentionCollapse>(),
            provider.GetRequiredService<ScoringOptions>(),
            NullLogger<ScoringEngine>.Instance);

        var strategyEngine = provider.GetRequiredService<IScoringStrategyFactory>().Primary.Engine;

        Assert.Equal(singleEngine.EffectiveConfig.Fingerprint, strategyEngine.EffectiveConfig.Fingerprint);
        Assert.Equal(singleEngine.EffectiveConfig, strategyEngine.EffectiveConfig);
    }

    [Fact]
    public void IScoringEngine_ResolvesToThePrimaryStrategyEngine()
    {
        using var provider = BuildDefaultGraph();

        var primary = provider.GetRequiredService<IScoringStrategyFactory>().Primary.Engine;

        // No dormant second engine: the ambient IScoringEngine IS the primary strategy's instance.
        Assert.Same(primary, provider.GetRequiredService<IScoringEngine>());
    }

    [Fact]
    public void Runtimes_AreBuiltOnce_AndCached()
    {
        using var provider = BuildDefaultGraph();

        var factory = provider.GetRequiredService<IScoringStrategyFactory>();

        Assert.Same(factory.Runtimes, factory.Runtimes);
        Assert.Same(factory.Primary.Engine, factory.Primary.Engine);
    }

    [Fact]
    public void StrategyName_IsNotAFingerprintInput()
    {
        // Two strategies over the SAME resolved weights differ only in name: their fingerprints must be
        // identical (a fingerprint identifies the effective scoring CONFIG, not the label on it), which is
        // exactly why the readable StrategyName is carried alongside it rather than folded into it.
        var weights = new ScoringWeights();
        var set = new ScoringStrategySet(
        [
            new ScoringStrategyDefinition("alpha", "default", weights, IsPrimary: true),
            new ScoringStrategyDefinition("beta", "default", weights, IsPrimary: false),
        ]);

        using var provider = BuildDefaultGraph();
        var factory = new ScoringStrategyFactory(
            set,
            provider.GetRequiredService<ISignalRepository>(),
            provider.GetRequiredService<ISignalFileStore>(),
            provider.GetRequiredService<IEvidenceRepository>(),
            new StrategyScopedScoreRepositoryFactory(provider.GetRequiredService<IScoreRepository>()),
            provider.GetRequiredService<ICompanyRepository>(),
            provider.GetRequiredService<IScoreFormulaFactory>(),
            provider.GetRequiredService<IAttentionSourceWeights>(),
            provider.GetRequiredService<ISignalSourceDescriptor>(),
            provider.GetRequiredService<InsiderMaterialityWeights>(),
            provider.GetRequiredService<MediaAttentionCollapse>(),
            provider.GetRequiredService<ScoringOptions>(),
            provider.GetRequiredService<ILogger<ScoringEngine>>());

        Assert.Equal(
            factory.Runtimes[0].Engine.EffectiveConfig.Fingerprint,
            factory.Runtimes[1].Engine.EffectiveConfig.Fingerprint);
    }

    [Fact]
    public void DeclaredSignalTypes_ReachTheStrategyEngine_AndReStampOnlyThatStrategy()
    {
        // Spec 138: the declared set travels definition → engine → fingerprint. Two strategies over identical
        // weights that differ ONLY in the signal types they consume must stamp different ScoringConfigVersions
        // (they are genuinely different scorings), while the unfiltered one keeps the untouched default stamp.
        var weights = new ScoringWeights();
        var set = new ScoringStrategySet(
        [
            new ScoringStrategyDefinition("everything", "default", weights, IsPrimary: true),
            new ScoringStrategyDefinition("insider-only", "default", weights, IsPrimary: false)
            {
                SignalTypes = SignalTypeFilter.Create([SignalType.InsiderBuying]),
            },
        ]);

        using var provider = BuildDefaultGraph();
        var factory = new ScoringStrategyFactory(
            set,
            provider.GetRequiredService<ISignalRepository>(),
            provider.GetRequiredService<ISignalFileStore>(),
            provider.GetRequiredService<IEvidenceRepository>(),
            new StrategyScopedScoreRepositoryFactory(provider.GetRequiredService<IScoreRepository>()),
            provider.GetRequiredService<ICompanyRepository>(),
            provider.GetRequiredService<IScoreFormulaFactory>(),
            provider.GetRequiredService<IAttentionSourceWeights>(),
            provider.GetRequiredService<ISignalSourceDescriptor>(),
            provider.GetRequiredService<InsiderMaterialityWeights>(),
            provider.GetRequiredService<MediaAttentionCollapse>(),
            provider.GetRequiredService<ScoringOptions>(),
            provider.GetRequiredService<ILogger<ScoringEngine>>());

        var unfiltered = factory.Runtimes[0].Engine.EffectiveConfig;
        var filtered = factory.Runtimes[1].Engine.EffectiveConfig;

        Assert.NotEqual(unfiltered.Fingerprint, filtered.Fingerprint);
        // The unfiltered strategy hashes the shared source descriptor VERBATIM (no signalTypes segment).
        var shared = provider.GetRequiredService<ISignalSourceDescriptor>().CanonicalDescriptor();
        Assert.Equal(shared, unfiltered.SignalSourceDescriptor);
        Assert.Equal($"{shared}signalTypes=InsiderBuying;", filtered.SignalSourceDescriptor);
    }

    [Fact]
    public void InlineWeightOverride_ReStampsOnlyThatStrategy()
    {
        // SPEC 149's identity criterion, VERIFIED rather than assumed. The claim being checked is that a
        // resolved ScoringWeights value is already hashed into ScoringConfigVersion by value, so an inline
        // Radar:Strategies[i].Weights override folds in for free — which is what stops two differently-tuned
        // strategies sharing one score series (and, via ScoreSeriesKey, one efficacy line).
        //
        // The weights are BOUND FROM CONFIG through the real AddRadarScoringStrategies here, not hand-built,
        // so this exercises the actual composition path an operator's JSON takes.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Radar:Strategies:0:Name"] = "baseline",
                ["Radar:Strategies:1:Name"] = "attention-light",
                // The ONLY difference between the two strategies.
                ["Radar:Strategies:1:Weights:OpportunityAttentionDiscountWeight"] = "0.25",
                ["Radar:PrimaryStrategy"] = "baseline",
            }).Build();

        var bound = new ServiceCollection();
        bound.AddRadarScoringStrategies(configuration);
        using var boundProvider = bound.BuildServiceProvider();
        var set = boundProvider.GetRequiredService<ScoringStrategySet>();

        using var provider = BuildDefaultGraph();
        var factory = new ScoringStrategyFactory(
            set,
            provider.GetRequiredService<ISignalRepository>(),
            provider.GetRequiredService<ISignalFileStore>(),
            provider.GetRequiredService<IEvidenceRepository>(),
            new StrategyScopedScoreRepositoryFactory(provider.GetRequiredService<IScoreRepository>()),
            provider.GetRequiredService<ICompanyRepository>(),
            provider.GetRequiredService<IScoreFormulaFactory>(),
            provider.GetRequiredService<IAttentionSourceWeights>(),
            provider.GetRequiredService<ISignalSourceDescriptor>(),
            provider.GetRequiredService<InsiderMaterialityWeights>(),
            provider.GetRequiredService<MediaAttentionCollapse>(),
            provider.GetRequiredService<ScoringOptions>(),
            provider.GetRequiredService<ILogger<ScoringEngine>>());

        var untuned = factory.Runtimes[0].Engine.EffectiveConfig;
        var tuned = factory.Runtimes[1].Engine.EffectiveConfig;

        Assert.NotEqual(untuned.Fingerprint, tuned.Fingerprint);
        // The tuned value is RECORDED, not merely hashed — the stamp has to dereference to something.
        Assert.Equal(0.25, tuned.Weights.OpportunityAttentionDiscountWeight);
        // …and the strategy that declared nothing keeps the untouched default stamp: an override re-stamps
        // its own strategy and nobody else's.
        Assert.Equal(new ScoringWeights(), untuned.Weights);
        Assert.Equal(
            provider.GetRequiredService<IScoringStrategyFactory>().Primary.Engine.EffectiveConfig.Fingerprint,
            untuned.Fingerprint);
    }

    [Fact]
    public void NonPrimaryStrategies_GetTheirOwnScoreRepository()
    {
        var shared = new InMemoryScoreRepository();
        var repositories = new StrategyScopedScoreRepositoryFactory(shared);

        var primary = new ScoringStrategyDefinition("alpha", "default", new ScoringWeights(), IsPrimary: true);
        var secondary = new ScoringStrategyDefinition("beta", "default", new ScoringWeights(), IsPrimary: false);

        Assert.Same(shared, repositories.ForStrategy(primary));
        Assert.NotSame(shared, repositories.ForStrategy(secondary));
        // Stable per strategy: a snapshot and its evidence links must land in the SAME store.
        Assert.Same(repositories.ForStrategy(secondary), repositories.ForStrategy(secondary));
    }
}
