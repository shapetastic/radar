using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.Scoring;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

public sealed class NewsAttentionCollectorDiTests
{
    [Fact]
    public void AddNewsAttentionCollector_RegistersCollector_WithValidOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNewsAttentionCollector(new NewsCollectorOptions
        {
            MaxRecordsPerCompany = 25,
            InterRequestDelay = TimeSpan.FromSeconds(1),
        });

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IEvidenceCollector>());
    }

    [Fact]
    public void AddNewsAttentionCollector_NullOptions_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => services.AddNewsAttentionCollector(null!));
    }

    [Fact]
    public void AddNewsAttentionCollector_NonPositiveMaxRecords_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddNewsAttentionCollector(new NewsCollectorOptions { MaxRecordsPerCompany = 0 }));

        Assert.Contains("Radar:News:MaxRecordsPerCompany", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddNewsAttentionCollector_NegativeInterRequestDelay_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddNewsAttentionCollector(new NewsCollectorOptions
            {
                InterRequestDelay = TimeSpan.FromSeconds(-1),
            }));

        Assert.Contains("Radar:News:InterRequestDelaySeconds", ex.Message, StringComparison.Ordinal);
    }

    // ---- spec 198 §1: the recency window is validated, and ZERO is legal ------------------------------

    [Fact]
    public void AddNewsAttentionCollector_NegativeRecencyWindow_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddNewsAttentionCollector(new NewsCollectorOptions
            {
                RecencyWindowDays = -1,
            }));

        Assert.Contains("Radar:News:RecencyWindowDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddNewsAttentionCollector_ZeroRecencyWindow_IsLegal_AndMeansDisabled()
    {
        // Zero is the DISABLED filter, not a misconfiguration: it reproduces the pre-198 unfiltered query
        // byte-for-byte, so rejecting it would remove the compatibility escape hatch.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNewsAttentionCollector(new NewsCollectorOptions { RecencyWindowDays = 0 });

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IEvidenceCollector>());
        Assert.Equal(0, provider.GetRequiredService<NewsCollectorOptions>().RecencyWindowDays);
    }

    [Fact]
    public void AddNewsAttentionCollector_DoesNotRegisterTheNewsQueryScoringIdentity()
    {
        // Spec 144/147 posture: the HASHED identity must be composable by a pass that registers no collector
        // at all, so it is the Worker's job in every run mode — never the collector registration's. If this
        // ever starts registering it, a standalone `score` pass would resolve a DIFFERENT identity from the
        // one its configuration describes.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNewsAttentionCollector(new NewsCollectorOptions { RecencyWindowDays = 7 });

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<NewsQueryScoringIdentity>());
    }

    [Fact]
    public void AddNewsAttentionCollector_DefaultOptions_CarryTheOneSharedWindowDefault()
    {
        // The shipped default is defined ONCE, on NewsQueryScoringIdentity, so the value a live run SENDS
        // and the value the fingerprint HASHES cannot drift.
        Assert.Equal(
            NewsQueryScoringIdentity.DefaultRecencyWindowDays,
            new NewsCollectorOptions().RecencyWindowDays);
    }

    [Fact]
    public void AddFileNewsObservationArchive_ExposesTheCompanyHistorySeam_OverTheSameSingleton()
    {
        // Spec 198 §2 / spec 142's "the repository IS the file store": one concrete instance, several
        // interfaces. A second instance would hydrate a second copy of the index.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFileNewsObservationArchive(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

        using var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<FileNewsObservationArchive>();

        Assert.Same(concrete, provider.GetRequiredService<INewsObservationCompanyHistory>());
        Assert.Same(concrete, provider.GetRequiredService<INewsObservationArchive>());
    }
}
