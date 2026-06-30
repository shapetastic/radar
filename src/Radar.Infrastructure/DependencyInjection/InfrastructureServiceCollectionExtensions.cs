using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.Infrastructure.Rss;
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
        services.AddSingleton<ISignalReviewRepository, InMemorySignalReviewRepository>();
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
        services.TryAddSingleton(new WeeklyReportOptions());
        services.AddSingleton<IWeeklyReportBuilder, WeeklyReportBuilder>();
        // The mapper is a core pipeline service used regardless of which collector is wired, so its
        // IEvidenceNormalizer dependency is registered here. TryAdd keeps a collector-specific
        // registration (e.g. AddLocalFileCollector) from conflicting.
        services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
        services.AddSingleton<CollectedEvidenceMapper>();
        return services;
    }

    /// <summary>
    /// Registers the deterministic local-file evidence collector along with the evidence
    /// normalizer the mapper depends on. The collector reads <c>*.json</c> evidence documents from
    /// <paramref name="sourceDirectory"/> and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> records (the
    /// <see cref="Radar.Application.Collectors.CollectedEvidenceMapper"/> normalizes/hashes them); it
    /// does not persist them. Intended for offline/test pipeline runs.
    /// </summary>
    public static IServiceCollection AddLocalFileCollector(
        this IServiceCollection services, string sourceDirectory)
    {
        services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
        services.AddSingleton(new LocalFileEvidenceCollectorOptions { SourceDirectory = sourceDirectory });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, LocalFileEvidenceCollector>();
        return services;
    }

    /// <summary>
    /// Registers the RSS press-release collector and the typed <c>HttpClient</c> its
    /// <see cref="IRssFeedReader"/> uses. The collector reads the per-company RSS feeds supplied on the
    /// <see cref="Radar.Application.Collectors.CollectionContext"/> (populated by the runner from
    /// <see cref="ICompanyRepository.GetSourceFeedsAsync"/>) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> press releases; it does not persist
    /// them. All HTTP/XML/Syndication code stays in Infrastructure (AD-5).
    /// </summary>
    public static IServiceCollection AddRssPressReleaseCollector(this IServiceCollection services)
    {
        services.AddHttpClient<IRssFeedReader, HttpRssFeedReader>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, RssPressReleaseCollector>();
        return services;
    }

    /// <summary>
    /// Registers the local-file company watch-universe seed source and the idempotent seeder. The seed file
    /// at <paramref name="filePath"/> defines the companies/aliases that entity resolution can match
    /// against. Safe to invoke the seeder on every startup (upsert-by-Id, AD-1).
    /// </summary>
    public static IServiceCollection AddLocalFileCompanySeed(
        this IServiceCollection services, string filePath)
    {
        services.AddSingleton(new LocalFileCompanySeedOptions { FilePath = filePath });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICompanySeedSource, LocalFileCompanySeedSource>();
        services.AddSingleton<ICompanyUniverseSeeder, CompanyUniverseSeeder>();
        return services;
    }

    /// <summary>
    /// Registers the insert-only file raw-evidence store that mirrors each newly-stored
    /// <see cref="Radar.Domain.Evidence.EvidenceItem"/> to
    /// <c>{rootDirectory}/{sourceType}/{yyyy}/{MM}/{contentHash}.json</c> (AD-8). The pipeline runner
    /// requires <see cref="Radar.Application.Evidence.IRawEvidenceStore"/>; all file I/O stays in
    /// Infrastructure. Existing raw files are never overwritten (provenance, AD-1).
    /// </summary>
    public static IServiceCollection AddFileRawEvidenceStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileRawEvidenceStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IRawEvidenceStore, FileRawEvidenceStore>();
        return services;
    }

    /// <summary>
    /// Registers the file report writer that writes each built weekly report's markdown to
    /// <c>{rootDirectory}/weekly/radar-weekly-{yyyy-MM-dd}.md</c>. The pipeline runner requires
    /// <see cref="Radar.Application.Reporting.IReportFileWriter"/>; all file I/O stays in
    /// Infrastructure. Reports are derived views, so an existing file may be overwritten (AD-1 governs
    /// evidence immutability only).
    /// </summary>
    public static IServiceCollection AddFileReportWriter(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileReportWriterOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IReportFileWriter, FileReportWriter>();
        return services;
    }

    /// <summary>
    /// Registers the end-to-end pipeline runner. Requires the persistence registration
    /// (<see cref="AddInMemoryRadarPersistence"/>), the application services
    /// (<see cref="AddRadarApplicationServices"/>), and an evidence collector
    /// (e.g. <see cref="AddLocalFileCollector"/>) to also be registered.
    /// </summary>
    public static IServiceCollection AddRadarPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton(new PipelineOptions());
        services.AddSingleton<IRadarPipeline, RadarPipelineRunner>();
        return services;
    }
}
