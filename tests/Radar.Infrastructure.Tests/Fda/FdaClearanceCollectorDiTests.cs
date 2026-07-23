using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Fda;

namespace Radar.Infrastructure.Tests.Fda;

public sealed class FdaClearanceCollectorDiTests
{
    [Fact]
    public void AddFdaClearanceCollector_RegistersCollectorAndReader()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFdaClearanceCollector(new FdaCollectorOptions());

        using var provider = services.BuildServiceProvider();

        var collector = Assert.Single(provider.GetServices<IEvidenceCollector>());
        Assert.Equal("fda", collector.CollectorName);

        Assert.NotNull(provider.GetService<IFdaClearanceReader>());
    }

    [Fact]
    public void AddFdaClearanceCollector_NullOptions_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddFdaClearanceCollector(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddFdaClearanceCollector_NonPositiveLookbackDays_FailsFast(int lookbackDays)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFdaClearanceCollector(new FdaCollectorOptions { LookbackDays = lookbackDays }));

        Assert.Contains("Radar:Fda:LookbackDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFdaClearanceCollector_NonPositiveMaxSampleClearances_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFdaClearanceCollector(new FdaCollectorOptions { MaxSampleClearances = 0 }));

        Assert.Contains("Radar:Fda:MaxSampleClearances", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFdaClearanceCollector_NonPositiveMaxPageSize_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFdaClearanceCollector(new FdaCollectorOptions { MaxPageSize = 0 }));

        Assert.Contains("Radar:Fda:MaxPageSize", ex.Message, StringComparison.Ordinal);
    }
}
