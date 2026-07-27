using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Collectors;
using Radar.Application.Scoring;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 151 — the composition ROOT's ordering. <c>AddRadarCollectorAttribution</c> must run BEFORE
/// <c>AddRadarApplicationServices</c>, or the library's <c>TryAddSingleton</c> defaults win and the setting
/// silently does nothing: the process would keep resolving only recorded attribution while an operator
/// believed a whole replayed backtest was reading the accrued store. Ordering bugs of this shape are exactly
/// what the "register configuration first" comments in <c>RadarWorkerServices</c> exist to prevent, so they
/// are pinned here rather than trusted.
/// </summary>
public sealed class CollectorAttributionWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"radar-attribution-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        // Every file-store root points into this test's own temp directory, so nothing writes cruft.
        (string Key, string Value)[] directories =
        [
            ("Radar:EvidenceSourceDirectory", Path.Combine(_root, "evidence")),
            ("Radar:EvidenceRawDirectory", Path.Combine(_root, "evidence-raw")),
            ("Radar:SignalsDirectory", Path.Combine(_root, "signals")),
            ("Radar:ScoresDirectory", Path.Combine(_root, "scores")),
            ("Radar:ReportDirectory", Path.Combine(_root, "reports")),
            ("Radar:RunsDirectory", Path.Combine(_root, "runs")),
            ("Radar:ScoringConfigsDirectory", Path.Combine(_root, "scoring-configs")),
        ];

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings
                    .Concat(directories)
                    .Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ShippedDefaults_LeaveInferenceOff_AndTheProvenanceStringUnmarked()
    {
        using var provider = BuildProvider();

        Assert.False(provider.GetRequiredService<CollectorAttributionOptions>().InferLegacyAttribution);
        Assert.IsType<RecordedOnlyCollectorAttributionResolver>(
            provider.GetRequiredService<ICollectorAttributionResolver>());
        Assert.DoesNotContain(
            "attribution=",
            provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingTheSetting_ReachesBothTheResolverAndTheRecordedProvenance()
    {
        using var provider = BuildProvider(("Radar:Scoring:InferLegacyCollectorAttribution", "true"));

        Assert.True(provider.GetRequiredService<CollectorAttributionOptions>().InferLegacyAttribution);
        Assert.IsNotType<RecordedOnlyCollectorAttributionResolver>(
            provider.GetRequiredService<ICollectorAttributionResolver>());
        Assert.Contains(
            "attribution=inferred-legacy;",
            provider.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingTheSetting_DoesNotMoveAnyStrategyFingerprint()
    {
        // THE no-fingerprint-move criterion in the composed app: attribution is DATA, not scoring
        // configuration. Every strategy's ScoringConfigVersion must be identical across the toggle while its
        // recorded CollectionProvenance differs — the same shape spec 141 asserted for a collector toggle.
        using var recordedOnly = BuildProvider();
        using var inferring = BuildProvider(("Radar:Scoring:InferLegacyCollectorAttribution", "true"));

        var before = recordedOnly.GetRequiredService<IScoringStrategyFactory>().Runtimes;
        var after = inferring.GetRequiredService<IScoringStrategyFactory>().Runtimes;

        Assert.NotEmpty(before);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(s => (s.Definition.Name, s.Engine.EffectiveConfig.Fingerprint)),
            after.Select(s => (s.Definition.Name, s.Engine.EffectiveConfig.Fingerprint)));

        Assert.NotEqual(
            recordedOnly.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance(),
            inferring.GetRequiredService<ISignalSourceDescriptor>().CollectionProvenance());
    }

    [Fact]
    public void UnparseableValue_FailsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("Radar:Scoring:InferLegacyCollectorAttribution", "yes")));

        Assert.Contains("Radar:Scoring:InferLegacyCollectorAttribution", ex.Message, StringComparison.Ordinal);
    }
}
