using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.News;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 177 §8: the <c>Radar:NewsResearch</c> block is FAIL-CLOSED (unknown keys / invalid limits fail
/// startup, per the specs-149/174 posture), capture defaults ON, the article fetch defaults OFF with an
/// empty allowlist, and the archive is a collection-side concern a standalone <c>score</c> pass never needs.
/// </summary>
public sealed class NewsResearchWorkerOptionsTests
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
    public void Default_RegistersTheArchive_ButNoContentReader_AndNoMigration()
    {
        // Shipped posture: CaptureRss defaults ON (pure observation), the fetch seam OFF (empty allowlist),
        // the migration OFF.
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<INewsObservationArchive>());
        Assert.Null(provider.GetService<INewsArticleContentReader>());
        Assert.Null(provider.GetService<INewsObservationMigration>());
    }

    [Fact]
    public void ScoreMode_NeverRegistersTheArchive()
    {
        // The archive is a collection-side concern; a score pass runs no collector and must not need it.
        using var provider = BuildProvider(("Radar:RunMode", "score"));

        Assert.Null(provider.GetService<INewsObservationArchive>());
    }

    [Fact]
    public void CaptureRssFalse_RegistersNoArchive()
    {
        using var provider = BuildProvider(("Radar:NewsResearch:CaptureRss", "false"));

        Assert.Null(provider.GetService<INewsObservationArchive>());
    }

    [Fact]
    public void UnknownKey_FailsStartup_NamingTheKeyAndTheValidNames()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:CapturRss", "true")));

        Assert.Contains("CapturRss", ex.Message);
        Assert.Contains("CaptureRss", ex.Message); // the valid-name list is rendered, not hand-waved
    }

    [Fact]
    public void UnknownNestedKey_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:ArticleFetch:AlowedDomains:0", "example.com")));

        Assert.Contains("AlowedDomains", ex.Message);
    }

    [Fact]
    public void UnparseableCaptureFlag_FailsStartup()
    {
        // ConfigurationBinder throws on a non-boolean value — reading "yes" as off would silently disable
        // exactly the capture the operator asked for.
        Assert.ThrowsAny<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:CaptureRss", "yes-please")));
    }

    [Theory]
    [InlineData("Radar:NewsResearch:ArticleFetch:TimeoutSeconds", "0")]
    [InlineData("Radar:NewsResearch:ArticleFetch:MaxResponseBytes", "-1")]
    public void InvalidFetchLimits_FailStartup_EvenWhileTheFetchIsDisabled(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider((key, value)));

        Assert.Contains("must be positive", ex.Message);
    }

    [Fact]
    public void FetchEnabledWithEmptyAllowlist_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(("Radar:NewsResearch:ArticleFetch:Enabled", "true")));

        Assert.Contains("AllowedDomains", ex.Message);
    }

    [Fact]
    public void FetchEnabledWithAllowlistButNoContactUserAgent_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
                ("Radar:NewsResearch:ArticleFetch:Enabled", "true"),
                ("Radar:NewsResearch:ArticleFetch:AllowedDomains:0", "example.com")));

        Assert.Contains("UserAgent", ex.Message);
    }

    [Fact]
    public void FetchEnabledWithAllowlistAndContactUserAgent_RegistersTheReader()
    {
        using var provider = BuildProvider(
            ("Radar:NewsResearch:ArticleFetch:Enabled", "true"),
            ("Radar:NewsResearch:ArticleFetch:AllowedDomains:0", "example.com"),
            ("Radar:NewsResearch:ArticleFetch:UserAgent", "RadarResearch contact@example.com"));

        Assert.NotNull(provider.GetService<INewsArticleContentReader>());
    }

    [Fact]
    public void RetrospectiveFetchWithoutTheFetchSeam_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
                ("Radar:NewsResearch:Migration:Enabled", "true"),
                ("Radar:NewsResearch:Migration:RetrospectiveFetch", "true")));

        Assert.Contains("ArticleFetch", ex.Message);
    }

    [Fact]
    public void MigrationCombinedWithReplay_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(
                ("Radar:NewsResearch:Migration:Enabled", "true"),
                ("Radar:Replay:Enabled", "true"),
                ("Radar:Replay:From", "2026-05-01"),
                ("Radar:Replay:To", "2026-05-02")));

        Assert.Contains("Migration", ex.Message);
    }

    [Fact]
    public void MigrationEnabled_RegistersTheMigration_AndTheArchiveItWritesThrough()
    {
        using var provider = BuildProvider(("Radar:NewsResearch:Migration:Enabled", "true"));

        Assert.NotNull(provider.GetService<INewsObservationMigration>());
        Assert.NotNull(provider.GetService<INewsObservationArchive>());
    }
}
