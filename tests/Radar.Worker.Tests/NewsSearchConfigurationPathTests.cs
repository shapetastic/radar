using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.News;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 190 §3 — two similarly named leaf keys, two different collectors. The NewsSearch path audited by
/// spec 190 is governed ONLY by <c>Radar:News:MaxRecordsPerCompany</c>; <c>Radar:Gdelt:MaxRecordsPerCompany</c>
/// belongs to the GDELT collector and is out of scope. Scoping the audit by symbol or text search would have
/// hit both, so the binding is pinned by CONFIGURATION PATH here rather than by name similarity. Both shipped
/// values stay 25.
/// </summary>
public sealed class NewsSearchConfigurationPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"radar-news-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The two keys are bound to DELIBERATELY DIFFERENT values, so a test that accidentally read the wrong
    /// one cannot pass. The newssearch reader/collector must see the <c>Radar:News</c> value.
    /// </summary>
    [Fact]
    public void NewsSearchCollector_BindsTheRadarNewsKey_NotTheSimilarlyNamedGdeltKey()
    {
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "newssearch"),
            ("Radar:Collectors:1", "news"),
            ("Radar:News:MaxRecordsPerCompany", "7"),
            ("Radar:Gdelt:MaxRecordsPerCompany", "99"));

        Assert.Equal(7, provider.GetRequiredService<NewsCollectorOptions>().MaxRecordsPerCompany);
        Assert.Equal(99, provider.GetRequiredService<GdeltCollectorOptions>().MaxRecordsPerCompany);
    }

    /// <summary>
    /// The reverse direction, so neither key can be reached through the other: changing only
    /// <c>Radar:Gdelt</c> leaves the newssearch limit at its shipped default.
    /// </summary>
    [Fact]
    public void GdeltKeyAlone_DoesNotMoveTheNewsSearchEffectiveLimit()
    {
        using var provider = BuildProvider(
            ("Radar:Collectors:0", "newssearch"),
            ("Radar:Collectors:1", "news"),
            ("Radar:Gdelt:MaxRecordsPerCompany", "99"));

        Assert.Equal(25, provider.GetRequiredService<NewsCollectorOptions>().MaxRecordsPerCompany);
    }

    /// <summary>
    /// Spec 190 raises NEITHER limit: both shipped values stay 25, in the code defaults and in the Worker's
    /// committed <c>appsettings.json</c>. The audit measures the local limit; it does not move it.
    /// </summary>
    [Fact]
    public void BothShippedLimitsRemain25()
    {
        Assert.Equal(25, new NewsWorkerOptions().MaxRecordsPerCompany);
        Assert.Equal(25, new GdeltWorkerOptions().MaxRecordsPerCompany);
        Assert.Equal(25, new NewsCollectorOptions().MaxRecordsPerCompany);
        Assert.Equal(25, new GdeltCollectorOptions().MaxRecordsPerCompany);

        using var appsettings = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));
        var radar = appsettings.RootElement.GetProperty("Radar");

        Assert.Equal(25, radar.GetProperty("News").GetProperty("MaxRecordsPerCompany").GetInt32());
        Assert.Equal(25, radar.GetProperty("Gdelt").GetProperty("MaxRecordsPerCompany").GetInt32());
    }

    private ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                .. settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)),
                .. TempDirectories(),
            ])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>Every file-store root pointed into this test's own temp directory, so nothing writes cruft.</summary>
    private KeyValuePair<string, string?>[] TempDirectories() =>
    [
        new("Radar:EvidenceSourceDirectory", Path.Combine(_root, "evidence")),
        new("Radar:EvidenceRawDirectory", Path.Combine(_root, "evidence-raw")),
        new("Radar:SignalsDirectory", Path.Combine(_root, "signals")),
        new("Radar:ScoresDirectory", Path.Combine(_root, "scores")),
        new("Radar:ReportDirectory", Path.Combine(_root, "reports")),
        new("Radar:RunsDirectory", Path.Combine(_root, "runs")),
        new("Radar:ScoringConfigsDirectory", Path.Combine(_root, "scoring-configs")),
    ];

    /// <summary>
    /// Walks up from the test binary to the repo root (the first ancestor carrying the Worker's
    /// <c>appsettings.json</c>) so the test does not depend on the working directory.
    /// </summary>
    private static string AppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Radar.Worker", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate src/Radar.Worker/appsettings.json from " + AppContext.BaseDirectory);
    }
}
