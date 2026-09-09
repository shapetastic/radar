using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Efficacy.FilingReads;
using Radar.Application.Filings;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 218 §6: <c>Radar:Efficacy:DirectionalFilingReads</c> is ENABLED by default INSIDE the already-opt-in
/// <c>Radar:Efficacy</c> gate (so the nightly baseline writes the measurement with no profile edit), and
/// registers nothing at all when efficacy itself is off — the default graph stays byte-unchanged.
/// </summary>
public sealed class DirectionalFilingReadsWorkerOptionsTests
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

        Assert.Null(provider.GetService<IDirectionalFilingReadReportGenerator>());
        Assert.Null(provider.GetService<IDirectionalFilingReadArtifactStore>());
    }

    [Fact]
    public void InsideTheEfficacyGate_ItIsEnabledByDefault()
    {
        using var provider = BuildProvider(("Radar:Efficacy:Enabled", "true"));

        Assert.NotNull(provider.GetService<IDirectionalFilingReadReportGenerator>());
        Assert.NotNull(provider.GetService<IDirectionalFilingReadArtifactStore>());
        Assert.NotNull(provider.GetService<DirectionalFilingReadReporter>());
        Assert.NotNull(provider.GetService<DirectionalFilingReadRenderer>());
    }

    [Fact]
    public void ItCanBeTurnedOffWithoutTouchingTheRestOfTheEfficacyGate()
    {
        using var provider = BuildProvider(
            ("Radar:Efficacy:Enabled", "true"),
            ("Radar:Efficacy:DirectionalFilingReads:Enabled", "false"));

        Assert.Null(provider.GetService<IDirectionalFilingReadReportGenerator>());
    }

    [Fact]
    public void WithNoAiEarningsRead_TheGeneratorStillResolves_AndTheCorpusSeamIsSimplyAbsent()
    {
        // The measurement must not fail to resolve just because the AI read is off: it reports
        // SeamNotRegistered rather than a fabricated zero.
        using var provider = BuildProvider(("Radar:Efficacy:Enabled", "true"));

        Assert.Null(provider.GetService<IAnalyzedFilingReadCorpus>());
        Assert.NotNull(provider.GetService<IDirectionalFilingReadReportGenerator>());
    }

    [Fact]
    public void WithTheAiEarningsReadOn_TheCorpusSeamAndTheCacheAreTheSameSingleton()
    {
        using var provider = BuildProvider(
            ("Radar:Efficacy:Enabled", "true"),
            ("Radar:Ai:Provider", "ollama"),
            ("Radar:Ai:Model", "llama3.1"),
            ("Radar:Sec:UserAgent", "Radar Tests test@example.com"));

        var cache = provider.GetService<IAnalyzedFilingCache>();
        var corpus = provider.GetService<IAnalyzedFilingReadCorpus>();

        Assert.NotNull(cache);
        Assert.NotNull(corpus);
        // ONE instance behind both seams: two would be two path/segment resolutions in disguise.
        Assert.Same(cache, corpus);
    }
}
