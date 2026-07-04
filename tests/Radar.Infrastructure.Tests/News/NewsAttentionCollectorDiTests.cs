using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
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
}
