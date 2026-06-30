using Microsoft.Extensions.DependencyInjection;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Infrastructure.DependencyInjection;

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
}
