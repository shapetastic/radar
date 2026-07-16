using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.Ai;
using Radar.Application.Collectors;
using Radar.Application.Efficacy;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Filings;
using Radar.Application.Pipeline;
using Radar.Application.Prices;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.Hiring;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.Infrastructure.Prices;
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
        // Attention source-quality tiering (spec 88): register the curated default tier map BEFORE the formula
        // so a composition root that bound its own Radar:Attention options via AddSingleton wins over this
        // default (mirrors the ScoringOptions pattern). ConfiguredAttentionSourceWeights validates the bound
        // options in its ctor (fails fast on a weight outside [0,1]).
        services.TryAddSingleton(AttentionSourceTierOptions.Default);
        services.TryAddSingleton<IAttentionSourceWeights, ConfiguredAttentionSourceWeights>();
        // Scoring magnitude weights (spec 89): the default == the radar-formula-v4 constants, so a blank/absent
        // config yields byte-identical v4 output. TryAdd keeps a composition-root-registered concrete
        // ScoringWeights (bound via AddRadarScoringWeights) winning over this default (mirrors ScoringOptions /
        // AttentionSourceTierOptions).
        services.TryAddSingleton(new ScoringWeights());
        services.TryAddSingleton<IScoreFormula, RadarScoreFormulaV5>();
        services.TryAddSingleton(new ScoringOptions());
        // Insider materiality magnitudes (spec 96): the default == the spec-93 buy/sell tiers + cluster boost,
        // so a blank/absent config yields byte-identical insider Strengths. TryAdd keeps a
        // composition-root-registered concrete InsiderMaterialityWeights (bound via AddRadarInsiderMateriality)
        // winning over this default (mirrors ScoringWeights). Injected into KeywordSignalExtractor and folded
        // into the ScoringConfigVersion fingerprint (via ScoringEngine).
        services.TryAddSingleton(new InsiderMaterialityWeights());
        // Signal-source descriptor (spec 95): folds the enabled collector NAMES + the extractor rule-set
        // identity into the ScoringConfigVersion fingerprint. DI resolves IEnumerable<IEvidenceCollector> at
        // RESOLUTION time, so this sees ALL collectors even though the Worker registers them AFTER
        // AddRadarApplicationServices. TryAdd lets a composition root substitute its own descriptor.
        services.TryAddSingleton<ISignalSourceDescriptor, SignalSourceDescriptor>();
        services.AddSingleton<IScoringEngine, ScoringEngine>();
        services.TryAddSingleton<IReportActionPolicy, WeeklyReportActionPolicyV1>();
        services.TryAddSingleton<IWeeklyReportRenderer, MarkdownWeeklyReportRenderer>();
        services.TryAddSingleton(new WeeklyReportOptions());
        // Collection-health validation (spec 98): reconciles seed-declared vs reached feed-type
        // inventory and warns on shrinkage (regression guard for the spec-97 feed-Id collision).
        // Diagnostic only — never evidence/signal/scoring input. Depends on ICompanySeedSource
        // (registered by AddLocalFileCompanySeed).
        services.TryAddSingleton<ICollectionHealthValidator, SeedFeedInventoryValidator>();
        services.AddSingleton<IWeeklyReportBuilder, WeeklyReportBuilder>();
        // The mapper is a core pipeline service used regardless of which collector is wired, so its
        // IEvidenceNormalizer dependency is registered here. TryAdd keeps a collector-specific
        // registration (e.g. AddLocalFileCollector) from conflicting.
        services.TryAddSingleton<IEvidenceNormalizer, EvidenceNormalizer>();
        services.AddSingleton<CollectedEvidenceMapper>();
        return services;
    }

    /// <summary>
    /// Resolves the effective scoring-weight profile and registers the concrete <see cref="ScoringWeights"/>
    /// as a singleton so it wins over the library's <c>TryAddSingleton</c> default (call this BEFORE
    /// <see cref="AddRadarApplicationServices"/>, mirroring the <c>Radar:Attention</c> binding). Precedence:
    /// <list type="bullet">
    /// <item><c>Radar:Scoring:Profile</c> selects a named profile; blank/absent ⇒ <c>"default"</c>.</item>
    /// <item>If <c>Radar:Scoring:Profiles:{name}</c> exists, its present fields bind ONTO a fresh
    /// <see cref="ScoringWeights"/> (unspecified fields keep the code default == v4).</item>
    /// <item>A <b>named</b> (non-default) profile that is requested but absent <b>fails fast</b> — a silent
    /// fallthrough to defaults would mask a typo'd profile name in an experiment.</item>
    /// <item>A blank/absent profile, or an absent <c>"default"</c> profile, ⇒ all code defaults
    /// (⇒ byte-identical v4 output and the pinned default fingerprint).</item>
    /// </list>
    /// The resolved weights are validated (<see cref="ScoringWeights.Validate"/>) so an out-of-range weight
    /// (e.g. <c>OpportunityAttentionDivisor = 0</c>) fails fast at registration, never silently distorting
    /// scoring.
    /// </summary>
    public static IServiceCollection AddRadarScoringWeights(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var name = configuration["Radar:Scoring:Profile"];
        var effectiveName = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        var section = configuration.GetSection($"Radar:Scoring:Profiles:{effectiveName}");

        ScoringWeights weights;
        if (section.Exists())
        {
            weights = section.Get<ScoringWeights>() ?? new ScoringWeights();
        }
        else if (!string.IsNullOrWhiteSpace(name)
            && !string.Equals(effectiveName, "default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Radar:Scoring:Profile '{effectiveName}' was requested but no matching profile exists under "
                    + "Radar:Scoring:Profiles — a named-but-missing profile is almost certainly a typo. Add the "
                    + $"profile under Radar:Scoring:Profiles:{effectiveName} or clear Radar:Scoring:Profile to use "
                    + "the code defaults.");
        }
        else
        {
            weights = new ScoringWeights();
        }

        // Fail fast at registration on a nonsensical weight (also enforced in the formula ctor).
        weights.Validate();

        services.AddSingleton(weights);
        return services;
    }

    /// <summary>
    /// Resolves the effective insider-materiality profile and registers the concrete
    /// <see cref="InsiderMaterialityWeights"/> as a singleton so it wins over the library's
    /// <c>TryAddSingleton</c> default (call this BEFORE <see cref="AddRadarApplicationServices"/>, mirroring
    /// <see cref="AddRadarScoringWeights"/>). Precedence:
    /// <list type="bullet">
    /// <item><c>Radar:Insider:Profile</c> selects a named profile; blank/absent ⇒ <c>"default"</c>.</item>
    /// <item>If <c>Radar:Insider:Profiles:{name}</c> exists, its present fields bind ONTO a fresh
    /// <see cref="InsiderMaterialityWeights"/> (unspecified fields keep the code default == spec 93).</item>
    /// <item>A <b>named</b> (non-default) profile that is requested but absent <b>fails fast</b> — a silent
    /// fallthrough to defaults would mask a typo'd profile name in an experiment.</item>
    /// <item>A blank/absent profile, or an absent <c>"default"</c> profile, ⇒ all code defaults
    /// (⇒ byte-identical spec-93 insider Strengths and the pinned default fingerprint).</item>
    /// </list>
    /// The resolved weights are validated (<see cref="InsiderMaterialityWeights.Validate"/>) so a
    /// misconfigured tier (an out-of-range Strength, a missing floor, a non-descending table) fails fast at
    /// registration, never silently producing a Strength that fails <c>SignalValidation</c> at runtime.
    /// </summary>
    public static IServiceCollection AddRadarInsiderMateriality(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var name = configuration["Radar:Insider:Profile"];
        var effectiveName = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        var section = configuration.GetSection($"Radar:Insider:Profiles:{effectiveName}");

        InsiderMaterialityWeights weights;
        if (section.Exists())
        {
            // Bind each list sub-section into a FRESH list (Get<List<T>> starts empty), overriding a whole
            // table only when the profile supplies it — otherwise keep the code default. Binding the record
            // directly with Get<InsiderMaterialityWeights>() would APPEND the profile's tiers onto the
            // default 5-tier table (the binder preserves existing collection items), producing a non-descending
            // table that fails Validate(); binding the tables explicitly gives clean replace-or-default semantics.
            var defaults = new InsiderMaterialityWeights();
            weights = defaults with
            {
                BuyTiers = BindTiersOrDefault(section.GetSection("BuyTiers"), defaults.BuyTiers),
                SellTiers = BindTiersOrDefault(section.GetSection("SellTiers"), defaults.SellTiers),
                ClusterBoost = section.GetValue("ClusterBoost", defaults.ClusterBoost),
            };
        }
        else if (!string.IsNullOrWhiteSpace(name)
            && !string.Equals(effectiveName, "default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Radar:Insider:Profile '{effectiveName}' was requested but no matching profile exists under "
                    + "Radar:Insider:Profiles — a named-but-missing profile is almost certainly a typo. Add the "
                    + $"profile under Radar:Insider:Profiles:{effectiveName} or clear Radar:Insider:Profile to use "
                    + "the code defaults.");
        }
        else
        {
            weights = new InsiderMaterialityWeights();
        }

        // Fail fast at registration on a misconfigured tier table (also enforced in the extractor ctor).
        weights.Validate();

        services.AddSingleton(weights);
        return services;
    }

    // Binds a tier table from its config sub-section into a FRESH list (clean replace semantics); returns the
    // supplied fallback (the code default) when the profile does not define the table at all.
    private static IReadOnlyList<InsiderMaterialityTier> BindTiersOrDefault(
        IConfigurationSection section, IReadOnlyList<InsiderMaterialityTier> fallback) =>
        section.Exists()
            ? section.Get<List<InsiderMaterialityTier>>() ?? fallback
            : fallback;

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
    /// Registers the SEC Form 4 (insider-transaction) collector and the typed <c>HttpClient</c> its
    /// <see cref="ISecForm4Reader"/> uses. The collector reads the per-company <c>secform4</c> feeds supplied
    /// on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is that
    /// company's EDGAR submissions JSON endpoint), fetches each Form 4's raw ownership XML, classifies its
    /// insider transactions by SEC transaction code (deterministic, NO AI), and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> filings carrying an insider-activity
    /// direction; it does not persist them. All HTTP/JSON/XML/SEC code stays in Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="SecForm4CollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent) or when
    /// <see cref="SecForm4CollectorOptions.MaxFilingsPerCompany"/> is zero/negative: each would let the
    /// collector run yet silently collect nothing, so they are treated as configuration errors. The named
    /// client sends the configured UA plus <c>Accept-Encoding: gzip, deflate</c> and enables automatic
    /// decompression (SEC recommends gzip).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSecForm4Collector(
        this IServiceCollection services, SecForm4CollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:SecForm4:UserAgent before enabling the \"secform4\" collector — every request 403s without it.");
        }

        if (options.MaxFilingsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "SEC Form 4 MaxFilingsPerCompany must be greater than zero; configure "
                    + "Radar:SecForm4:MaxFilingsPerCompany to a positive cap (default 15) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<ISecForm4Reader, HttpSecForm4Reader>(client =>
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
        services.AddSingleton<IEvidenceCollector, SecForm4Collector>();
        return services;
    }

    /// <summary>
    /// Registers the SEC Schedule 13D/13G (institutional/activist beneficial-ownership) collector and the
    /// typed <c>HttpClient</c> its <see cref="ISec13DGReader"/> uses. The collector reads the per-company
    /// <c>sec13dg</c> feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each
    /// feed's <c>Url</c> is that company's EDGAR submissions JSON endpoint), filters <c>filings.recent</c> to
    /// the 13D/13G form types, classifies each by form (deterministic, NO AI, metadata-only — no filing body
    /// fetch), and produces raw <see cref="Radar.Application.Collectors.CollectedEvidence"/> filings carrying
    /// the fixed spec-99 ownership phrases; it does not persist them. All HTTP/JSON/SEC code stays in
    /// Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="Sec13DGCollectorOptions.UserAgent"/> is null/blank (SEC returns HTTP 403 for
    /// every request without a compliant declared User-Agent) or when
    /// <see cref="Sec13DGCollectorOptions.MaxFilingsPerCompany"/> is zero/negative: each would let the
    /// collector run yet silently collect nothing, so they are treated as configuration errors. The named
    /// client sends the configured UA plus <c>Accept-Encoding: gzip, deflate</c> and enables automatic
    /// decompression (SEC recommends gzip).
    /// </para>
    /// </summary>
    public static IServiceCollection AddSec13DGCollector(
        this IServiceCollection services, Sec13DGCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:Sec13DG:UserAgent before enabling the \"sec13dg\" collector — every request 403s without it.");
        }

        if (options.MaxFilingsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "SEC 13D/13G MaxFilingsPerCompany must be greater than zero; configure "
                    + "Radar:Sec13DG:MaxFilingsPerCompany to a positive cap (default 20) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<ISec13DGReader, HttpSec13DGReader>(client =>
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
        services.AddSingleton<IEvidenceCollector, Sec13DGCollector>();
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
    /// <para>
    /// The optional <paramref name="readerOptions"/> tunes the reader's bounded HTTP 429 backoff-retry
    /// (spec 105) — SEC 429s the Archives burst this reader fires, starving the AI directional path. It
    /// defaults to <see cref="SecEarningsReleaseReaderOptions"/>'s defaults (2 retries, 2s base backoff) and is
    /// registered with <c>TryAdd</c> so the reader resolves it. Registration fails fast when
    /// <see cref="SecEarningsReleaseReaderOptions.MaxRetriesOn429"/> is negative or
    /// <see cref="SecEarningsReleaseReaderOptions.RetryBackoff"/> is negative.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSecEarningsReleaseReader(
        this IServiceCollection services,
        SecCollectorOptions options,
        SecEarningsReleaseReaderOptions? readerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            throw new InvalidOperationException(
                "SEC EDGAR requires a compliant User-Agent (e.g. \"Radar Research <email>\"); configure "
                    + "Radar:Sec:UserAgent before enabling the SEC earnings-release reader — every request 403s without it.");
        }

        readerOptions ??= new SecEarningsReleaseReaderOptions();

        if (readerOptions.MaxRetriesOn429 < 0)
        {
            throw new InvalidOperationException(
                "SEC earnings-release MaxRetriesOn429 must not be negative; configure Radar:Sec:MaxRetriesOn429 "
                    + "to a non-negative retry count (default 2) — a negative value is nonsensical configuration.");
        }

        if (readerOptions.RetryBackoff < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "SEC earnings-release RetryBackoff must not be negative; configure Radar:Sec:RetryBackoffSeconds "
                    + "to a non-negative base delay (default 2s) — the reader doubles it per 429 retry.");
        }

        if (readerOptions.MinRequestInterval < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "SEC earnings-release MinRequestInterval must not be negative; configure Radar:Sec:MinRequestIntervalMs "
                    + "to a non-negative pace in milliseconds (default 250 ms) — a negative value is nonsensical configuration.");
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
        services.TryAddSingleton(readerOptions);
        services.TryAddSingleton(TimeProvider.System);
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
    /// Registers the Google News RSS news-attention collector (Radar's third-party market-attention source
    /// that is NOT per-IP throttled — the fix for GDELT's per-IP quota) and the typed <c>HttpClient</c> its
    /// <see cref="INewsSearchReader"/> uses. The collector reads the per-company <c>newssearch</c> feeds
    /// supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/> (each feed's <c>Url</c> is
    /// a <c>query=...&amp;ticker=...</c> token) and produces raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> news articles; it does not persist them.
    /// All HTTP/XML/source specifics stay in Infrastructure behind the reader (AD-5). This is a DISTINCT kind
    /// from the GDELT <c>news</c> collector, so both can be enabled independently.
    /// <para>
    /// Fails fast when <see cref="NewsCollectorOptions.MaxRecordsPerCompany"/> is zero/negative or when
    /// <see cref="NewsCollectorOptions.InterRequestDelay"/> is negative: each would let the collector run yet
    /// either collect nothing or carry nonsensical config, so they are treated as configuration errors. The
    /// endpoint needs no User-Agent or key (Google News RSS is keyless); the named client only enables
    /// automatic gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddNewsAttentionCollector(
        this IServiceCollection services, NewsCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRecordsPerCompany <= 0)
        {
            throw new InvalidOperationException(
                "News search MaxRecordsPerCompany must be greater than zero; configure "
                    + "Radar:News:MaxRecordsPerCompany to a positive cap (default 25) — a zero/negative value "
                    + "collects nothing while still running.");
        }

        if (options.InterRequestDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "News search InterRequestDelay must not be negative; configure Radar:News:InterRequestDelaySeconds "
                    + "to a non-negative pacing delay (default 1s) — Google News RSS is not per-IP throttled, so a small polite pace suffices.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<INewsSearchReader, HttpNewsSearchReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, NewsAttentionCollector>();
        return services;
    }

    /// <summary>
    /// Registers the ATS job-board hiring collector (spec 103) and the two named typed <c>HttpClient</c>s
    /// its per-platform <see cref="IJobBoardReader"/>s use (Greenhouse and Lever have different JSON
    /// shapes, so each platform gets its own reader + client). The collector reads the per-company
    /// <c>hiringats</c> feeds supplied on the <see cref="Radar.Application.Collectors.CollectionContext"/>
    /// (each feed's <c>Url</c> is a <c>platform=…&amp;board=…</c> token) and produces exactly one raw
    /// <see cref="Radar.Application.Collectors.CollectedEvidence"/> open-role snapshot per board carrying
    /// the fixed spec-103 hiring phrase; it does not persist them. All HTTP/JSON code stays in
    /// Infrastructure (AD-5).
    /// <para>
    /// Fails fast when <see cref="HiringCollectorOptions.MaxSampleTitles"/> is negative: a negative sample
    /// bound is nonsensical configuration (zero is valid — it simply omits the metadata title sample). The
    /// APIs need no User-Agent or key (verified keyless access); the named clients only enable automatic
    /// gzip/deflate decompression (polite).
    /// </para>
    /// </summary>
    public static IServiceCollection AddHiringBoardCollector(
        this IServiceCollection services, HiringCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxSampleTitles < 0)
        {
            throw new InvalidOperationException(
                "Hiring MaxSampleTitles must not be negative; configure Radar:Hiring:MaxSampleTitles to a "
                    + "non-negative sample bound (default 5) — a negative value is nonsensical configuration.");
        }

        services.AddSingleton(options);

        services.AddHttpClient<GreenhouseBoardReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        services.AddHttpClient<LeverBoardReader>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        // Surface both typed-client readers through the IJobBoardReader seam the collector's
        // platform→reader map consumes.
        services.AddSingleton<IJobBoardReader>(sp => sp.GetRequiredService<GreenhouseBoardReader>());
        services.AddSingleton<IJobBoardReader>(sp => sp.GetRequiredService<LeverBoardReader>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEvidenceCollector, HiringBoardCollector>();
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
    /// Registers Radar's first real AI capability: the config-selected <see cref="IChatClient"/>-backed
    /// <see cref="IFilingAnalyzer"/> (<see cref="ChatFilingAnalyzer"/>, singleton), which turns an earnings-release
    /// plain text (spec 73) into a typed, validated <see cref="Radar.Domain.Filings.FilingSentiment"/> — a
    /// directional read AS REPORTED (improving vs deteriorating trajectory), never advice. It does NOT register an
    /// <see cref="IChatClient"/>: it depends on the singleton client that <see cref="AddRadarAi"/> already
    /// registered, so call this only after (and inside the same opt-in gate as) <see cref="AddRadarAi"/>. All
    /// model-calling code stays in Infrastructure and uses only <c>Microsoft.Extensions.AI</c> abstractions (AD-5).
    /// <para>
    /// Fails fast when <paramref name="options"/> is null, or when
    /// <see cref="FilingAnalyzerOptions.MaxInputLength"/> is zero/negative: a non-positive cap would truncate
    /// every release to nothing (or throw at the substring), so it is treated as a configuration error rather
    /// than surfacing as an opaque failure at first use.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRadarFilingAnalyzer(
        this IServiceCollection services, FilingAnalyzerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxInputLength <= 0)
        {
            throw new InvalidOperationException(
                "Radar AI filing analyzer requires a positive input cap; configure Radar:Ai:MaxInputLength "
                    + "to a positive character count (default 12000) — a zero/negative value would truncate every "
                    + "earnings release to nothing.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IFilingAnalyzer, ChatFilingAnalyzer>();
        return services;
    }

    /// <summary>
    /// Registers the opt-in directional filing-signal source (<see cref="DirectionalFilingSignalSource"/>,
    /// singleton) behind the Application <see cref="IDirectionalFilingSignalSource"/> seam. For an
    /// in-window earnings 8-K (form 8-K + item 2.02) it composes the merged
    /// <see cref="ISecEarningsReleaseReader"/> (EX-99.1 body) and <see cref="IFilingAnalyzer"/> (typed
    /// directional read) into at most one confidence-gated directional <c>GuidanceChange</c> signal
    /// (Improving -&gt; Positive, Deteriorating -&gt; Negative). It depends on the reader and analyzer, so
    /// call this only inside the same opt-in gate as (and after) <see cref="AddSecEarningsReleaseReader"/>
    /// and <see cref="AddRadarFilingAnalyzer"/>; it does NOT register either of those here. All HTTP/AI
    /// specifics stay behind the injected interfaces (AD-5).
    /// <para>
    /// Fails fast when <paramref name="options"/> is null, when
    /// <see cref="DirectionalFilingSignalOptions.MinConfidence"/> is outside [0,1], or when
    /// <see cref="DirectionalFilingSignalOptions.MaxFilingsPerRun"/> is zero/negative: each is a
    /// configuration error that would otherwise gate every read to nothing or surface as an opaque failure.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDirectionalFilingSignals(
        this IServiceCollection services, DirectionalFilingSignalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MinConfidence is < 0m or > 1m)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a confidence gate in [0,1]; configure "
                    + "Radar:Ai:MinConfidence (default 0.6) — a value outside [0,1] can never gate a signal.");
        }

        if (options.MaxFilingsPerRun <= 0)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a positive per-run cap; configure "
                    + "Radar:Ai:MaxFilingsPerRun (default 5) — a zero/negative value analyzes nothing while still running.");
        }

        if (options.MaxConsecutiveRateLimited < 0)
        {
            throw new InvalidOperationException(
                "Radar directional filing signals require a non-negative 429 circuit-breaker threshold; configure "
                    + "Radar:Ai:MaxConsecutiveRateLimited (default 2, 0 disables) — a negative value is nonsensical configuration.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IDirectionalFilingSignalSource, DirectionalFilingSignalSource>();
        return services;
    }

    /// <summary>
    /// Registers the file-backed per-accession earnings-analysis-result cache (spec 107,
    /// <see cref="FileAnalyzedFilingCache"/>) behind the Application <see cref="IAnalyzedFilingCache"/> seam,
    /// writing one <c>{accession}.json</c> under <paramref name="rootDirectory"/> via the shared
    /// <c>GracefulFileWriter</c> + <c>RadarFileStoreJson.Options</c> scaffolding (fail-safe reads → cache miss).
    /// <see cref="DirectionalFilingSignalSource"/> consumes it to replay a previously-analyzed filing instead of
    /// re-fetching the same <c>www.sec.gov</c> exhibit every run. This is an AD-14 analogue: operational/reference
    /// data, NOT an <see cref="IEvidenceCollector"/>, not evidence, not a signal source, and not a
    /// scoring/fingerprint input — it only changes WHETHER a fetch happens, never the signal that is scored.
    /// </summary>
    public static IServiceCollection AddFileAnalyzedFilingCache(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileAnalyzedFilingCacheOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IAnalyzedFilingCache, FileAnalyzedFilingCache>();
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
    /// Registers the content-addressed effective-scoring-config store that writes the resolved scoring
    /// config to <c>{rootDirectory}/{fingerprint}.json</c> once per distinct config (spec 91), completing
    /// the spec-89 provenance chain: a snapshot's <c>ScoringConfigVersion</c> stamp dereferences back to the
    /// weights that produced it. The pipeline runner requires <see cref="IScoringConfigStore"/>; all file
    /// I/O stays in Infrastructure. Insert-if-new (immutable, AD-1 mirror): an existing file is never
    /// overwritten.
    /// </summary>
    public static IServiceCollection AddFileScoringConfigStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileScoringConfigStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IScoringConfigStore, FileScoringConfigStore>();
        return services;
    }

    /// <summary>
    /// Registers the keyless Yahoo chart v8 daily price-history reader behind the Application
    /// <see cref="IPriceHistoryReader"/> seam (AD-14), plus the typed <c>HttpClient</c> it uses (browser
    /// <c>User-Agent</c> + gzip/deflate decompression, mirroring <see cref="AddSecEarningsReleaseReader"/>). This
    /// is a SEPARATE seam from the evidence collectors: it is NOT an <see cref="IEvidenceCollector"/>, produces no
    /// <c>CollectedEvidence</c>, and is not added to <c>Radar:Collectors</c>. All HTTP/JSON/Yahoo specifics stay in
    /// Infrastructure (AD-5). No key/secret/paid service.
    /// <para>
    /// Fails fast when <paramref name="range"/> is not a known Yahoo <c>validRanges</c> value — a typo'd range
    /// would otherwise silently return an empty series. <c>PriceReaderOptions</c> stays Infrastructure-internal
    /// (AD-5); the caller supplies only the range token.
    /// </para>
    /// </summary>
    public static IServiceCollection AddHttpPriceHistoryReader(
        this IServiceCollection services, string range)
    {
        var options = new PriceReaderOptions { Range = range };

        // Fail fast at registration on an invalid range.
        options.Validate();

        services.AddSingleton(options);

        services.AddHttpClient<IPriceHistoryReader, HttpPriceHistoryReader>(client =>
            {
                // The Yahoo chart endpoint requires a browser-like User-Agent (verified) but no cookie/crumb.
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            });

        return services;
    }

    /// <summary>
    /// Registers the file price-history reference store that persists each ticker's daily bars to
    /// <c>{rootDirectory}/{ticker}.json</c> (AD-14) via the shared <c>GracefulFileWriter</c> +
    /// <c>RadarFileStoreJson.Options</c> scaffolding, merging/deduping bars by <c>Date</c> (last-write-wins per
    /// date, ascending). Consumers require the Application <see cref="IPriceHistoryStore"/>; all file I/O stays in
    /// Infrastructure. This store is consumed by NOTHING in the scoring/evidence/signal/report path — it exists
    /// solely for a future price-efficacy validation/backtest spec.
    /// </summary>
    public static IServiceCollection AddFilePriceHistoryStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FilePriceHistoryStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IPriceHistoryStore, FilePriceHistoryStore>();
        return services;
    }

    /// <summary>
    /// Registers the file efficacy-artifact store that writes each company's price-efficacy SVG + CSV to
    /// <c>{rootDirectory}/{ticker}.{svg,csv}</c> (AD-14 read side) via the shared <c>GracefulFileWriter</c>,
    /// keyed by the shared <c>FileTickerKey</c> (the same on-disk ticker key the price store uses). Consumers
    /// require the Application <see cref="IEfficacyArtifactStore"/>; all file I/O stays in Infrastructure. This
    /// store writes ONLY efficacy artifacts — never evidence/signal/score.
    /// </summary>
    public static IServiceCollection AddFileEfficacyArtifactStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.AddSingleton(new FileEfficacyArtifactStoreOptions { RootDirectory = rootDirectory });
        services.AddSingleton<IEfficacyArtifactStore, FileEfficacyArtifactStore>();
        return services;
    }

    /// <summary>
    /// Registers the opt-in price-efficacy reporting step (AD-14 read side): the <see cref="EfficacyDatasetBuilder"/>
    /// (the deterministic no-look-ahead JOIN over score history + price), the pure <see cref="EfficacySvgRenderer"/>
    /// + <see cref="EfficacyCsvRenderer"/>, and the <see cref="IEfficacyReportGenerator"/> that composes them. It
    /// depends on <see cref="ICompanyRepository"/>, <see cref="IScoreSnapshotFileStore"/>,
    /// <see cref="IPriceHistoryStore"/> (all read-only) and <see cref="IEfficacyArtifactStore"/>; call
    /// <see cref="AddFileEfficacyArtifactStore"/> alongside it. It has NO evidence/signal/scoring write dependency
    /// and runs OUTSIDE <c>IRadarPipeline</c>.
    /// </summary>
    public static IServiceCollection AddRadarEfficacyReport(this IServiceCollection services)
    {
        services.AddSingleton<EfficacyDatasetBuilder>();
        services.AddSingleton<EfficacySvgRenderer>();
        services.AddSingleton<EfficacyCsvRenderer>();
        services.AddSingleton<IEfficacyReportGenerator, EfficacyReportGenerator>();
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
