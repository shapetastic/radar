using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
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
    /// Registers the stateless application services as singletons: the deterministic
    /// <see cref="Radar.Application.EntityResolution.ICompanyResolver"/> and the deterministic
    /// keyword-based <see cref="Radar.Application.SignalExtraction.ISignalExtractor"/>
    /// (<see cref="KeywordSignalExtractor"/>). The resolver only depends on the singleton
    /// repositories and the extractor is dependency-free, so a singleton lifetime is correct and
    /// lets singleton consumers (e.g. a hosted service) resolve them from the root provider.
    /// Requires <see cref="AddInMemoryRadarPersistence"/> (or another registration of the
    /// repositories) to have been called for the resolver's dependencies.
    /// </summary>
    public static IServiceCollection AddRadarApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<ICompanyResolver, CompanyResolver>();
        services.AddSingleton<ISignalExtractor, KeywordSignalExtractor>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISignalReviewer, DeterministicSignalReviewer>();
        services.TryAddSingleton<IScoreFormula, RadarScoreFormulaV1>();
        services.TryAddSingleton(new ScoringOptions());
        services.AddSingleton<IScoringEngine, ScoringEngine>();
        services.TryAddSingleton<IReportActionPolicy, WeeklyReportActionPolicyV1>();
        services.TryAddSingleton<IWeeklyReportRenderer, MarkdownWeeklyReportRenderer>();
        return services;
    }

    /// <summary>
    /// Registers the deterministic local-file evidence collector along with the evidence
    /// normalizer it depends on. The collector reads <c>*.json</c> evidence documents from
    /// <paramref name="sourceDirectory"/> and produces immutable <see cref="Radar.Domain.Evidence.EvidenceItem"/>
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
