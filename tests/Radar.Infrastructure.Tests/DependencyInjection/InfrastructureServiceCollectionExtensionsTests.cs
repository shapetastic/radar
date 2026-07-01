using Microsoft.Extensions.DependencyInjection;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.DependencyInjection;

public class InfrastructureServiceCollectionExtensionsTests
{
    [Fact]
    public void AddInMemoryRadarPersistence_RegistersRepositoriesOnly_NoResolver()
    {
        var services = new ServiceCollection().AddInMemoryRadarPersistence();

        Assert.Contains(services, d => d.ServiceType == typeof(IEvidenceRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(ICompanyRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(ISignalRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(ISignalReviewRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(IScoreRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(IReportRepository));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ICompanyResolver));

        // "Repositories only" is the contract: exactly the six repository registrations and
        // nothing else, so an accidental extra registration is caught here.
        Assert.Equal(6, services.Count);
    }

    [Fact]
    public void AddRadarApplicationServices_RegistersResolverAsSingleton()
    {
        var services = new ServiceCollection().AddRadarApplicationServices();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ICompanyResolver));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void Resolver_ResolvesFromRootProvider_AndIsSameInstance()
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddInMemoryRadarPersistence()
            .AddRadarApplicationServices()
            .BuildServiceProvider();

        // Resolve directly from the root provider (no scope) to guard against the
        // scoped-from-root regression.
        var first = provider.GetRequiredService<ICompanyResolver>();
        var second = provider.GetRequiredService<ICompanyResolver>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddSecEdgarCollector_ValidOptions_RegistersCollector()
    {
        var services = new ServiceCollection().AddSecEdgarCollector(
            new SecCollectorOptions { UserAgent = "Radar Research test@example.com" });

        Assert.Contains(services, d => d.ServiceType == typeof(IEvidenceCollector));
    }

    [Fact]
    public void AddSecEdgarCollector_BlankUserAgent_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddSecEdgarCollector(new SecCollectorOptions { UserAgent = "  " }));

        Assert.Contains("User-Agent", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddSecEdgarCollector_NonPositiveMaxFilings_FailsFast(int maxFilings)
    {
        // A zero/negative cap would let the collector run yet collect nothing — treat it as a config error.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddSecEdgarCollector(new SecCollectorOptions
            {
                UserAgent = "Radar Research test@example.com",
                MaxFilingsPerCompany = maxFilings,
            }));

        Assert.Contains("MaxFilingsPerCompany", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSecEdgarCollector_EmptyForms_FailsFast()
    {
        // An empty form filter matches nothing, so the collector would run yet collect nothing.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddSecEdgarCollector(new SecCollectorOptions
            {
                UserAgent = "Radar Research test@example.com",
                Forms = [],
            }));

        Assert.Contains("filing form", ex.Message, StringComparison.Ordinal);
    }
}
