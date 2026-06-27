using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Evidence;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the thread-safe in-memory repository implementations as singletons so the
    /// in-memory store persists for the lifetime of the run. Intended for deterministic tests
    /// and local pipeline runs; the Postgres/Dapper repositories arrive in a later task behind
    /// the same interfaces.
    /// </summary>
    public static IServiceCollection AddInMemoryRadarPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IEvidenceRepository, InMemoryEvidenceRepository>();
        services.AddSingleton<ICompanyRepository, InMemoryCompanyRepository>();
        services.AddSingleton<ISignalRepository, InMemorySignalRepository>();
        services.AddSingleton<IScoreRepository, InMemoryScoreRepository>();
        services.AddSingleton<IReportRepository, InMemoryReportRepository>();
        return services;
    }

    /// <summary>
    /// Registers the deterministic local-file evidence collector along with the evidence
    /// normalizer it depends on. The collector reads <c>*.json</c> evidence documents from
    /// <paramref name="sourceDirectory"/> and produces immutable <see cref="Domain.Evidence.EvidenceItem"/>
    /// records; it does not persist them. Intended for offline/test pipeline runs.
    /// </summary>
    public static IServiceCollection AddLocalFileCollector(
        this IServiceCollection services, string sourceDirectory)
    {
        services.AddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
        services.AddSingleton(new LocalFileEvidenceCollectorOptions { SourceDirectory = sourceDirectory });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, LocalFileEvidenceCollector>();
        return services;
    }
}
