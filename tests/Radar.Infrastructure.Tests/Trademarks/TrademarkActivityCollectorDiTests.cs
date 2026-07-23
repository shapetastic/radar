using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Collectors;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Trademarks;

namespace Radar.Infrastructure.Tests.Trademarks;

public sealed class TrademarkActivityCollectorDiTests
{
    [Fact]
    public void AddTrademarkActivityCollector_RegistersCollectorAndReader()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTrademarkActivityCollector(new TrademarkCollectorOptions());

        using var provider = services.BuildServiceProvider();

        var collector = Assert.Single(provider.GetServices<IEvidenceCollector>());
        Assert.Equal("trademarks", collector.CollectorName);

        Assert.NotNull(provider.GetService<ITrademarkSearchReader>());
    }

    [Fact]
    public void AddTrademarkActivityCollector_NullOptions_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddTrademarkActivityCollector(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddTrademarkActivityCollector_NonPositiveLookbackDays_FailsFast(int lookbackDays)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTrademarkActivityCollector(new TrademarkCollectorOptions { LookbackDays = lookbackDays }));

        Assert.Contains("Radar:Trademarks:LookbackDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTrademarkActivityCollector_NonPositiveMaxSampleMarks_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTrademarkActivityCollector(new TrademarkCollectorOptions { MaxSampleMarks = 0 }));

        Assert.Contains("Radar:Trademarks:MaxSampleMarks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTrademarkActivityCollector_NonPositiveMaxPageSize_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTrademarkActivityCollector(new TrademarkCollectorOptions { MaxPageSize = 0 }));

        Assert.Contains("Radar:Trademarks:MaxPageSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTrademarkActivityCollector_BlankApiKeyEnvVar_FailsFast()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTrademarkActivityCollector(new TrademarkCollectorOptions { ApiKeyEnvVar = "  " }));

        Assert.Contains("Trademarks ApiKeyEnvVar", ex.Message, StringComparison.Ordinal);
    }
}
