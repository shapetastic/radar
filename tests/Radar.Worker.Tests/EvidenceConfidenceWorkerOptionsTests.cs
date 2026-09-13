using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Efficacy.EvidenceConfidence;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 225: <c>Radar:Efficacy:EvidenceConfidence</c> is ENABLED by default INSIDE the already-opt-in
/// <c>Radar:Efficacy</c> gate (so the nightly baseline writes the measurement with no profile edit), registers
/// nothing when efficacy itself is off, and is NOT registered for a collect pass — a collect pass scores
/// nothing, so the artifact is absent-but-not-fatal there.
/// </summary>
public sealed class EvidenceConfidenceWorkerOptionsTests
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
    public void WithEfficacyDisabled_NothingIsRegistered()
    {
        using var provider = BuildProvider();

        Assert.Null(provider.GetService<IEvidenceConfidenceDistributionGenerator>());
        Assert.Null(provider.GetService<IEvidenceConfidenceArtifactStore>());
    }

    [Fact]
    public void InsideTheEfficacyGate_ItIsEnabledByDefault_AndResolves()
    {
        using var provider = BuildProvider(("Radar:Efficacy:Enabled", "true"));

        Assert.NotNull(provider.GetService<IEvidenceConfidenceDistributionGenerator>());
        Assert.NotNull(provider.GetService<IEvidenceConfidenceArtifactStore>());
        Assert.NotNull(provider.GetService<EvidenceConfidenceDistributionReporter>());
        Assert.NotNull(provider.GetService<EvidenceConfidenceDistributionRenderer>());
    }

    [Fact]
    public void ItCanBeTurnedOffWithoutTouchingTheRestOfTheEfficacyGate()
    {
        using var provider = BuildProvider(
            ("Radar:Efficacy:Enabled", "true"),
            ("Radar:Efficacy:EvidenceConfidence:Enabled", "false"));

        Assert.Null(provider.GetService<IEvidenceConfidenceDistributionGenerator>());
    }

    [Fact]
    public void ACollectPass_DoesNotRegisterIt_BecauseNothingScores()
    {
        using var provider = BuildProvider(
            ("Radar:Efficacy:Enabled", "true"),
            ("Radar:RunMode", "collect"),
            ("Radar:Collectors:0", "rss"));

        Assert.Null(provider.GetService<IEvidenceConfidenceDistributionGenerator>());
    }

    [Fact]
    public void AScorePass_RegistersIt_BecauseItScores()
    {
        using var provider = BuildProvider(
            ("Radar:Efficacy:Enabled", "true"),
            ("Radar:RunMode", "score"));

        Assert.NotNull(provider.GetService<IEvidenceConfidenceDistributionGenerator>());
    }
}
