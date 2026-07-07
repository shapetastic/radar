using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Hiring;

namespace Radar.Infrastructure.Tests.Hiring;

public sealed class HiringBoardCollectorDiTests
{
    [Fact]
    public void AddHiringBoardCollector_RegistersCollectorAndBothPlatformReaders()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddHiringBoardCollector(new HiringCollectorOptions());

        using var provider = services.BuildServiceProvider();

        var collector = Assert.Single(provider.GetServices<IEvidenceCollector>());
        Assert.Equal("hiring-ats", collector.CollectorName);

        // Both platform readers surface through the IJobBoardReader seam (one per JSON shape).
        var readers = provider.GetServices<IJobBoardReader>().ToArray();
        Assert.Equal(2, readers.Length);
        Assert.Contains(readers, r => r.Platform == "greenhouse");
        Assert.Contains(readers, r => r.Platform == "lever");
    }

    [Fact]
    public void AddHiringBoardCollector_NullOptions_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => services.AddHiringBoardCollector(null!));
    }

    [Fact]
    public void AddHiringBoardCollector_NegativeMaxSampleTitles_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddHiringBoardCollector(new HiringCollectorOptions { MaxSampleTitles = -1 }));

        Assert.Contains("Radar:Hiring:MaxSampleTitles", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddHiringBoardCollector_ZeroMaxSampleTitles_IsValid()
    {
        // Zero simply omits the metadata title sample — a valid configuration, unlike a negative bound.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddHiringBoardCollector(new HiringCollectorOptions { MaxSampleTitles = 0 });

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IEvidenceCollector>());
    }
}
