using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Patents;

namespace Radar.Infrastructure.Tests.Patents;

public sealed class PatentActivityCollectorDiTests
{
    [Fact]
    public void AddPatentActivityCollector_RegistersCollectorAndReader()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPatentActivityCollector(new PatentCollectorOptions());

        using var provider = services.BuildServiceProvider();

        var collector = Assert.Single(provider.GetServices<IEvidenceCollector>());
        Assert.Equal("patents", collector.CollectorName);

        Assert.NotNull(provider.GetService<IPatentSearchReader>());
    }

    [Fact]
    public void AddPatentActivityCollector_NullOptions_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddPatentActivityCollector(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddPatentActivityCollector_NonPositiveLookbackDays_FailsFast(int lookbackDays)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddPatentActivityCollector(new PatentCollectorOptions { LookbackDays = lookbackDays }));

        Assert.Contains("Radar:Patents:LookbackDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPatentActivityCollector_NonPositiveMaxSampleTitles_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddPatentActivityCollector(new PatentCollectorOptions { MaxSampleTitles = 0 }));

        Assert.Contains("Radar:Patents:MaxSampleTitles", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPatentActivityCollector_NonPositiveMaxPageSize_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddPatentActivityCollector(new PatentCollectorOptions { MaxPageSize = 0 }));

        Assert.Contains("Radar:Patents:MaxPageSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPatentActivityCollector_BlankApiKeyEnvVar_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddPatentActivityCollector(new PatentCollectorOptions { ApiKeyEnvVar = "  " }));

        Assert.Contains("Patents ApiKeyEnvVar", ex.Message, StringComparison.Ordinal);
    }
}
