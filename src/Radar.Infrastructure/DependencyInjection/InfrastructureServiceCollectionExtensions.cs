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
using Radar.Application.Signals;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.Infrastructure.Rss;
using Radar.Infrastructure.Sec;
using Radar.Infrastructure.Sources;
using Radar.Infrastructure.UsaSpending;

using System.Net;

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
        services.TryAddSingleton<IScoreFormula, RadarScoreFormulaV2>();
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
    /// Registers the SEC EDGAR filing collector and the typed <c>HttpClient</c> its
    /// <see cref="ISecFilingReader"/> uses. The collector reads the per-company <c>sec</c> feeds supplied on
    /// the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is that
    /// company's EDGAR submissions JSON endpoint) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> filings; it does not persist them. All
    /// HTTP/JSON/SEC code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="SecCollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent), when
    /// <see cref="SecCollectorOptions.MaxFilingsPerCompany"/> is zero/negative, or when
    /// <see cref="SecCollectorOptions.Forms"/> is null/empty: each of those would let the collector run yet
    /// silently collect nothing, so they are treated as configuration errors. The named client sends the
    /// configured UA plus <c>Accept-Encoding: gzip, deflate</c> and enables automatic decompression (SEC
    /// recommends gzip).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSecEdgarCollector(
        this IServiceCollection services, SecCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:Sec:UserAgent before enabling the \"sec\" collector — every request 403s without it.");
        }

        if (options.MaxFilingsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "SEC EDGAR MaxFilingsPerCompany must be greater than zero; configure Radar:Sec:MaxFilingsPerCompany "
                    + "to a positive cap (default 25) — a zero/negative value collects nothing while still running.");
        }

        if (options.Forms is null || options.Forms.Count == 0)
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires at least one filing form to collect; configure Radar:Sec:Forms "
                    + "(default 8-K, 10-Q, 10-K) — an empty list collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<ISecFilingReader, HttpSecFilingReader>(client =>
            {
                // Use TryAddWithoutValidation: the SEC-recommended UA form ("Radar Research <email>") is not a
                // strict RFC product/comment token, so the strongly-typed UserAgent collection rejects it.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, SecEdgarFilingCollector>();
        return services;
    }

    /// <summary>
    /// Registers the USASpending.gov government-contract collector and the typed <c>HttpClient</c> its
    /// <see cref="IUsaSpendingAwardReader"/> uses. The collector reads the per-company <c>usaspending</c>
    /// feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's
    /// <c>Url</c> is a <c>recipientId=...&amp;recipientSearchText=...</c> token) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> contract awards; it does not persist
    /// them. All HTTP/JSON/USASpending code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="UsaSpendingCollectorOptions.AwardTypeCodes"/> is null/empty, when
    /// <see cref="UsaSpendingCollectorOptions.MaxAwardsPerCompany"/> is zero/negative, or when
    /// <see cref="UsaSpendingCollectorOptions.LookbackDays"/> is zero/negative: each of those would let the
    /// collector run yet silently collect nothing, so they are treated as configuration errors. The API needs
    /// no User-Agent or key; the named client only enables automatic gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddUsaSpendingContractCollector(
        this IServiceCollection services, UsaSpendingCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AwardTypeCodes is null || options.AwardTypeCodes.Count == 0)
        {
            throw new InvalidOperationException(
                "USASpending requires at least one award_type_code to query; configure "
                    + "Radar:UsaSpending:AwardTypeCodes (default A, B, C, D — the contracts group) — an empty "
                    + "list collects nothing while still running.");
        }

        if (options.MaxAwardsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "USASpending MaxAwardsPerCompany must be greater than zero; configure "
                    + "Radar:UsaSpending:MaxAwardsPerCompany to a positive cap (default 25) — a zero/negative "
                    + "value collects nothing while still running.");
        }

        if (options.LookbackDays <= 0)
        {
            throw new InvalidOperationException(
                "USASpending LookbackDays must be greater than zero; configure Radar:UsaSpending:LookbackDays "
                    + "to a positive window (default 365) — a zero/negative value collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<IUsaSpendingAwardReader, HttpUsaSpendingAwardReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, UsaSpendingContractCollector>();
        return services;
    }

    /// <summary>
    /// Registers the GDELT DOC 2.0 news collector (Radar's first third-party market-attention source) and the
    /// typed <c>HttpClient</c> its <see cref="IGdeltNewsReader"/> uses. The collector reads the per-company
    /// <c>news</c> feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each
    /// feed's <c>Url</c> is a <c>query=...&amp;ticker=...</c> token) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> news articles; it does not persist them.
    /// All HTTP/JSON/GDELT code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="GdeltCollectorOptions.MaxRecordsPerCompany"/> is zero/negative, when
    /// <see cref="GdeltCollectorOptions.Timespan"/> is null/blank, when
    /// <see cref="GdeltCollectorOptions.InterRequestDelay"/> is negative, or when
    /// <see cref="GdeltCollectorOptions.MaxRetriesOn429"/> is negative: each of those would let the collector
    /// run yet either collect nothing, hammer GDELT's aggressive rate limit, or carry nonsensical config, so
    /// they are treated as configuration errors. The API needs no User-Agent or key; the named client only enables automatic
    /// gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddGdeltNewsCollector(
        this IServiceCollection services, GdeltCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRecordsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "GDELT MaxRecordsPerCompany must be greater than zero; configure "
                    + "Radar:Gdelt:MaxRecordsPerCompany to a positive cap (default 25) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        if (string.IsNullOrWhiteSpace(options.Timespan))
        {
            throw new InvalidOperationException(
                "GDELT requires a non-blank timespan window; configure Radar:Gdelt:Timespan (default 2w) — a "
                    + "blank value collects nothing while still running.");
        }

        if (options.InterRequestDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "GDELT InterRequestDelay must not be negative; configure Radar:Gdelt:InterRequestDelaySeconds "
                    + "to a non-negative pacing delay (default 3s) — GDELT throttles hard, so pacing is required.");
        }

        if (options.MaxRetriesOn429 < 0)
        {
            throw new InvalidOperationException(
                "GDELT MaxRetriesOn429 must not be negative; configure Radar:Gdelt:MaxRetriesOn429 to a "
                    + "non-negative retry count (default 1) — a negative value is nonsensical configuration.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<IGdeltNewsReader, HttpGdeltNewsReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, GdeltNewsCollector>();
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
    /// Registers the file signal store that mirrors each reviewed
    /// <see cref="Radar.Domain.Signals.Signal"/> (with its embedded review) to
    /// <c>{rootDirectory}/{yyyy}/{MM}/{signalId}.json</c> (AD-8). The pipeline runner requires
    /// <see cref="Radar.Application.Signals.ISignalFileStore"/>; all file I/O stays in Infrastructure.
    /// Signals are upsert-by-Id, so an existing file is overwritten last-write-wins (AD-1 governs
    /// evidence immutability only).
    /// </summary>
    public static IServiceCollection AddFileSignalStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileSignalStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<ISignalFileStore, FileSignalStore>();
        return services;
    }

    /// <summary>
    /// Registers the file score-snapshot store that mirrors each
    /// <see cref="Radar.Domain.Scoring.CompanyScoreSnapshot"/> together with its
    /// <see cref="Radar.Domain.Scoring.ScoreEvidenceLink"/>s to
    /// <c>{rootDirectory}/{companyId}/{snapshotId}.json</c> (AD-8). The pipeline runner requires
    /// <see cref="Radar.Application.Scoring.IScoreSnapshotFileStore"/>; all file I/O stays in
    /// Infrastructure. Snapshots are upsert-by-Id, so an existing file is overwritten last-write-wins
    /// (AD-1 governs evidence immutability only).
    /// </summary>
    public static IServiceCollection AddFileScoreStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileScoreSnapshotStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IScoreSnapshotFileStore, FileScoreSnapshotStore>();
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
    /// Registers the file pipeline-run store that writes one <see cref="PipelineRunRecord"/> per
    /// completed run to <c>{rootDirectory}/{yyyy}/{MM}/run-...json</c> (AD-8), the append-only run log.
    /// The pipeline runner requires <see cref="IPipelineRunStore"/>; all file I/O stays in Infrastructure.
    /// Each run carries a fresh id, so files never collide and prior runs are never overwritten.
    /// </summary>
    public static IServiceCollection AddFilePipelineRunStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FilePipelineRunStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IPipelineRunStore, FilePipelineRunStore>();
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
