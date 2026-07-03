using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Ai;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Infrastructure.Ai;
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
    /// Registers the SEC EDGAR earnings-release (EX-99.1) body reader and the typed <c>HttpClient</c> its
    /// <see cref="ISecEarningsReleaseReader"/> uses. Given a filing's CIK + dashed accession, the reader
    /// fetches the filing index, selects the <c>EX-99.1</c> earnings-release exhibit (with an <c>EX-99.*</c>
    /// fallback; never the primary 8-K), fetches it, and strips it to plain text via the shared
    /// <see cref="IEvidenceNormalizer"/>. This is a standalone service (the analyzer in a later slice injects
    /// it); it is <b>not</b> an <see cref="IEvidenceCollector"/> and is <b>not</b> added to
    /// <c>Radar:Collectors</c>, so default pipeline behaviour is unchanged. All HTTP/HTML/SEC code stays in
    /// Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="SecCollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent). The named client sends the configured UA plus
    /// <c>Accept-Encoding: gzip, deflate</c> and enables automatic decompression (SEC recommends gzip).
    /// <see cref="SecCollectorOptions"/> and <see cref="IEvidenceNormalizer"/> are registered with
    /// <c>TryAdd</c> so this method coexists with <see cref="AddSecEdgarCollector"/> and
    /// <see cref="AddRadarApplicationServices"/> without a double-registration conflict, and resolves the
    /// reader's stripper dependency even when wired standalone.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSecEarningsReleaseReader(
        this IServiceCollection services, SecCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:Sec:UserAgent before enabling the SEC earnings-release reader — every request 403s without it.");
        }

        services.AddHttpClient<ISecEarningsReleaseReader, HttpSecEarningsReleaseReader>(client =>
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

        services.TryAddSingleton(options);
        services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
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
                    + "to a non-negative pacing delay (default 6s) — GDELT allows ~1 request/5s per IP, so pacing is required.");
        }

        if (options.MaxRetriesOn429 < 0)
        {
            throw new InvalidOperationException(
                "GDELT MaxRetriesOn429 must not be negative; configure Radar:Gdelt:MaxRetriesOn429 to a "
                    + "non-negative retry count (default 2) — a negative value is nonsensical configuration.");
        }

        if (options.RetryBackoff < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "GDELT RetryBackoff must not be negative; configure Radar:Gdelt:RetryBackoffSeconds to a "
                    + "non-negative base cool-down (default 60s) — the reader doubles it per 429 retry.");
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
    /// Registers Radar's config-driven AI chat-client seam: the <see cref="IChatClientFactory"/> (singleton) and a
    /// factory-produced singleton provider-neutral <see cref="IChatClient"/>, so future consumers can inject either.
    /// The provider is fixed at startup by <see cref="AiClientOptions.Provider"/> (case-insensitive) — <c>"anthropic"</c>
    /// (hosted) or <c>"ollama"</c> (local, keyless). All concrete provider SDK types stay in Infrastructure (AD-5).
    /// Uses plain <c>AddSingleton</c> — the provider SDKs manage their own HTTP transport, so no named <c>HttpClient</c>
    /// is wired. There is no consumer of the client yet; this only proves a config-selected client can be obtained.
    /// <para>
    /// Fails fast when <see cref="AiClientOptions.Provider"/> is blank or unknown, when <see cref="AiClientOptions.Model"/>
    /// is blank, when the <c>anthropic</c> provider has a blank <see cref="AiClientOptions.AnthropicApiKey"/>, or when the
    /// <c>ollama</c> provider has a blank or non-absolute-URI <see cref="AiClientOptions.OllamaEndpoint"/>: each of those is
    /// a configuration error that would otherwise surface as an opaque failure at first use. The provider is validated
    /// first so a blank provider yields the provider message, not a spurious key/endpoint message.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarAi(
        this IServiceCollection services, AiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Normalize (trim) every config string once so validation and the registered singleton agree, and so trailing
        // whitespace from env vars / copied JSON can't defeat the URI parse or reach the provider SDK.
        options = new AiClientOptions
        {
            Provider = options.Provider?.Trim() ?? string.Empty,
            Model = options.Model?.Trim() ?? string.Empty,
            AnthropicApiKey = options.AnthropicApiKey?.Trim() ?? string.Empty,
            OllamaEndpoint = options.OllamaEndpoint?.Trim() ?? string.Empty,
        };

        var provider = options.Provider;
        var isAnthropic = string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase);
        var isOllama = string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase);

        if (!isAnthropic && !isOllama)
        {
            throw new InvalidOperationException(
                "Radar AI requires a supported provider; configure Radar:Ai:Provider to \"anthropic\" (hosted) or "
                    + "\"ollama\" (local, keyless) — a blank/unknown value has no client to build.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException(
                "Radar AI requires a model id; configure Radar:Ai:Model (e.g. \"claude-opus-4-8\" for anthropic or "
                    + "an installed tag like \"llama3.1\" for ollama) — a blank value has no model to call.");
        }

        if (isAnthropic && string.IsNullOrWhiteSpace(options.AnthropicApiKey))
        {
            throw new InvalidOperationException(
                "Radar AI \"anthropic\" is a hosted provider and requires an API key; configure Radar:Ai:Anthropic:ApiKey "
                    + "before selecting the anthropic provider — every request fails without it.");
        }

        if (isOllama && !Uri.TryCreate(options.OllamaEndpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "Radar AI \"ollama\" requires an absolute endpoint URI; configure Radar:Ai:Ollama:Endpoint "
                    + "(default http://localhost:11434) — a blank or relative value cannot address the local Ollama server.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<IChatClientFactory>().Create());
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
