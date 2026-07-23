using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Fcc;

namespace Radar.Infrastructure.Tests.Fcc;

public sealed class FccEquipmentAuthorizationCollectorDiTests
{
    [Fact]
    public void AddFccEquipmentAuthorizationCollector_RegistersCollectorAndReader()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFccEquipmentAuthorizationCollector(new FccCollectorOptions());

        using var provider = services.BuildServiceProvider();

        var collector = Assert.Single(provider.GetServices<IEvidenceCollector>());
        Assert.Equal("fccauth", collector.CollectorName);

        Assert.NotNull(provider.GetService<IFccAuthReader>());
    }

    [Fact]
    public void AddFccEquipmentAuthorizationCollector_NullOptions_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddFccEquipmentAuthorizationCollector(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddFccEquipmentAuthorizationCollector_NonPositiveLookbackDays_FailsFast(int lookbackDays)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFccEquipmentAuthorizationCollector(
                new FccCollectorOptions { LookbackDays = lookbackDays }));

        Assert.Contains("Radar:Fcc:LookbackDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFccEquipmentAuthorizationCollector_NonPositiveMaxSampleAuthorizations_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFccEquipmentAuthorizationCollector(
                new FccCollectorOptions { MaxSampleAuthorizations = 0 }));

        Assert.Contains("Radar:Fcc:MaxSampleAuthorizations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFccEquipmentAuthorizationCollector_NonPositiveMaxPageSize_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFccEquipmentAuthorizationCollector(
                new FccCollectorOptions { MaxPageSize = 0 }));

        Assert.Contains("Radar:Fcc:MaxPageSize", ex.Message, StringComparison.Ordinal);
    }
}
