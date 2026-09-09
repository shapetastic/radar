using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Acquisitions;
using Radar.Application.Pipeline;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Worker.Tests;

/// <summary>
/// SPEC 217 §1/§2 — the composition. Two facts this proves that no unit test can: the recognition path
/// RESOLVES end-to-end when a SEC User-Agent is configured (a silently-unresolvable pass would leave every
/// acquisition unrecognised while the whole suite stayed green — the spec-150 lesson), and it is COMPLETELY
/// INERT without one, so a deployment with no UA behaves exactly as it did before spec 217.
/// </summary>
public sealed class AcquisitionRecognitionWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"radar-acq-wiring-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ServiceProvider BuildProvider(params (string Key, string Value)[] extra)
    {
        var settings = new List<(string Key, string Value)>
        {
            ("Radar:EvidenceSourceDirectory", Path.Combine(_root, "evidence")),
            ("Radar:EvidenceRawDirectory", Path.Combine(_root, "evidence-raw")),
            ("Radar:SignalsDirectory", Path.Combine(_root, "signals")),
            ("Radar:ScoresDirectory", Path.Combine(_root, "scores")),
            ("Radar:ReportDirectory", Path.Combine(_root, "reports")),
            ("Radar:RunsDirectory", Path.Combine(_root, "runs")),
            ("Radar:ScoringConfigsDirectory", Path.Combine(_root, "scoring-configs")),
            ("Radar:AcquisitionsDirectory", Path.Combine(_root, "acquisitions")),
            ("Radar:AcquisitionScanCacheDirectory", Path.Combine(_root, "acquisitions-cache")),
        };
        settings.AddRange(extra);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WithASecUserAgent_TheWholeRecognitionPathResolves()
    {
        using var provider = BuildProvider(("Radar:Sec:UserAgent", "Radar Test test@example.com"));

        Assert.NotNull(provider.GetService<IAcquisitionRecognitionPass>());
        Assert.NotNull(provider.GetService<IAcquisitionStore>());
        Assert.NotNull(provider.GetService<IAcquisitionScanCache>());
        Assert.NotNull(provider.GetService<IAcquisitionFilingBodyReader>());

        // The store-backed source WINS over the library's inert TryAdd default — otherwise scoring, the
        // report and the efficacy comparison would all read an empty projection while the pass wrote real
        // records.
        Assert.IsType<AcquisitionStorePendingSource>(
            provider.GetRequiredService<IPendingAcquisitionSource>());

        // And the pipeline runner actually picks it up (its parameter is optional by design).
        Assert.NotNull(provider.GetService<IRadarPipeline>());
    }

    [Fact]
    public void WithoutASecUserAgent_TheFeatureIsCompletelyInert()
    {
        // SEC 403s every request without a compliant UA, so registering the pass would guarantee a run of
        // fetch failures rather than a recognition. The whole path is absent and the INERT projection wins.
        using var provider = BuildProvider();

        Assert.Null(provider.GetService<IAcquisitionRecognitionPass>());
        Assert.Null(provider.GetService<IAcquisitionStore>());
        Assert.IsType<NoPendingAcquisitionSource>(
            provider.GetRequiredService<IPendingAcquisitionSource>());
        Assert.NotNull(provider.GetService<IRadarPipeline>());
    }

    [Fact]
    public void ExplicitlyDisabled_IsAlsoInert_EvenWithAUserAgent()
    {
        using var provider = BuildProvider(
            ("Radar:Sec:UserAgent", "Radar Test test@example.com"),
            ("Radar:Acquisitions:Enabled", "false"));

        Assert.Null(provider.GetService<IAcquisitionRecognitionPass>());
        Assert.IsType<NoPendingAcquisitionSource>(
            provider.GetRequiredService<IPendingAcquisitionSource>());
    }

    [Fact]
    public void ANonPositiveFetchBudget_FailsFast_RatherThanRunningAndRecognisingNothing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(
            ("Radar:Sec:UserAgent", "Radar Test test@example.com"),
            ("Radar:Acquisitions:MaxFetchesPerRun", "0")));

        Assert.Contains("MaxFetchesPerRun", ex.Message, StringComparison.Ordinal);
    }
}
