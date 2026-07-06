using Radar.Application.Pipeline;
using Radar.Application.Prices;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Sec;
using Radar.Infrastructure.UsaSpending;

namespace Radar.Worker;

/// <summary>
/// Composes the full Radar pipeline dependency graph from configuration. Lives in an
/// <c>internal static</c> helper so <see cref="Program"/> stays a few lines and the graph is
/// unit-testable without launching a host.
/// </summary>
internal static class RadarWorkerServices
{
    public static IServiceCollection AddRadarWorker(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Radar").Get<RadarWorkerOptions>() ?? new RadarWorkerOptions();

        // Fail fast with a clear message: a non-positive interval would otherwise throw an opaque
        // ArgumentOutOfRangeException from PeriodicTimer when the worker starts looping.
        if (!options.RunOnce && options.IntervalMinutes <= 0)
        {
            throw new InvalidOperationException(
                $"Radar:IntervalMinutes must be greater than zero when Radar:RunOnce is false (was {options.IntervalMinutes}).");
        }

        // Register the configured option instances FIRST so the libraries' TryAddSingleton defaults
        // (ScoringOptions / WeeklyReportOptions / PipelineOptions) do not override them. Do NOT reorder
        // these below the AddRadar* helpers — that would let configuration lose to the library defaults.
        services.AddSingleton(new ScoringOptions { Window = TimeSpan.FromDays(options.ScoringWindowDays) });
        services.AddSingleton(new WeeklyReportOptions
        {
            Period = TimeSpan.FromDays(options.ReportPeriodDays),
            MaxItems = options.ReportMaxItems,
            RecentRunsInReport = options.RecentRunsInReport,
        });
        services.AddSingleton(new PipelineOptions { GenerateReport = options.GenerateReport });
        services.AddSingleton(new WorkerRunOptions
        {
            RunOnce = options.RunOnce,
            Interval = TimeSpan.FromMinutes(options.IntervalMinutes),
        });

        // Attention source-quality tiers (spec 88): bind the optional Radar:Attention section and register it
        // BEFORE AddRadarApplicationServices so configuration wins over the library default (its TryAddSingleton
        // is a no-op once this concrete instance is registered). Falls back to the curated code default when the
        // section is absent/null. ConfiguredAttentionSourceWeights validates the bound options at startup and
        // fails fast on an invalid weight.
        services.AddSingleton(
            configuration.GetSection("Radar:Attention").Get<AttentionSourceTierOptions>()
                ?? AttentionSourceTierOptions.Default);

        // Scoring magnitude weights (spec 89): resolve the Radar:Scoring:Profile / Profiles selection and
        // register the concrete ScoringWeights BEFORE AddRadarApplicationServices so configuration wins over
        // the library default (its TryAddSingleton is a no-op once this concrete instance is registered).
        // A blank/absent profile binds all code defaults == v4 (byte-identical). Fails fast on a
        // named-but-missing profile or an invalid weight.
        services.AddRadarScoringWeights(configuration);

        // Insider materiality magnitudes (spec 96): resolve the Radar:Insider:Profile / Profiles selection and
        // register the concrete InsiderMaterialityWeights BEFORE AddRadarApplicationServices so configuration
        // wins over the library default (its TryAddSingleton is a no-op once this concrete instance is
        // registered). A blank/absent profile binds all code defaults == spec 93 (byte-identical). Fails fast
        // on a named-but-missing profile or an invalid tier table.
        services.AddRadarInsiderMateriality(configuration);

        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();

        // Enable the configured evidence collectors additively (case-insensitive). Each kind registers
        // its collector as IEvidenceCollector, composing into the IEnumerable the runner now consumes.
        // Fail fast with a clear message on an empty list or an unknown kind, mirroring the interval
        // check above. De-dupe defensively so a config typo listing the same kind twice registers once.
        if (options.Collectors is null || options.Collectors.Count == 0)
        {
            throw new InvalidOperationException(
                "Radar:Collectors must enable at least one collector; valid kinds are \"rss\", \"localfile\", \"sec\", \"secform4\", \"sec13dg\", \"usaspending\", \"news\", and \"newssearch\".");
        }

        var seenKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawKind in options.Collectors)
        {
            // Validate/normalize first so a null/empty/whitespace entry fails fast with a clear
            // message instead of falling through to the "unknown kind" branch as "kind '' ...".
            if (string.IsNullOrWhiteSpace(rawKind))
            {
                throw new InvalidOperationException(
                    "Radar:Collectors entries must not be null, empty, or whitespace; valid kinds are \"rss\", \"localfile\", \"sec\", \"secform4\", \"sec13dg\", \"usaspending\", \"news\", and \"newssearch\".");
            }

            var kind = rawKind.Trim();
            if (!seenKinds.Add(kind))
            {
                continue;
            }

            if (string.Equals(kind, "rss", StringComparison.OrdinalIgnoreCase))
            {
                services.AddRssPressReleaseCollector();
            }
            else if (string.Equals(kind, "localfile", StringComparison.OrdinalIgnoreCase))
            {
                services.AddLocalFileCollector(options.EvidenceSourceDirectory);
            }
            else if (string.Equals(kind, "sec", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSecEdgarCollector(new SecCollectorOptions
                {
                    UserAgent = options.Sec.UserAgent,
                    Forms = options.Sec.Forms,
                    MaxFilingsPerCompany = options.Sec.MaxFilingsPerCompany,
                });
            }
            else if (string.Equals(kind, "secform4", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSecForm4Collector(new SecForm4CollectorOptions
                {
                    UserAgent = options.SecForm4.UserAgent,
                    MaxFilingsPerCompany = options.SecForm4.MaxFilingsPerCompany,
                });
            }
            else if (string.Equals(kind, "sec13dg", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSec13DGCollector(new Sec13DGCollectorOptions
                {
                    UserAgent = options.Sec13DG.UserAgent,
                    MaxFilingsPerCompany = options.Sec13DG.MaxFilingsPerCompany,
                });
            }
            else if (string.Equals(kind, "usaspending", StringComparison.OrdinalIgnoreCase))
            {
                services.AddUsaSpendingContractCollector(new UsaSpendingCollectorOptions
                {
                    AwardTypeCodes = options.UsaSpending.AwardTypeCodes,
                    LookbackDays = options.UsaSpending.LookbackDays,
                    MaxAwardsPerCompany = options.UsaSpending.MaxAwardsPerCompany,
                });
            }
            else if (string.Equals(kind, "news", StringComparison.OrdinalIgnoreCase))
            {
                services.AddGdeltNewsCollector(new GdeltCollectorOptions
                {
                    Timespan = options.Gdelt.Timespan,
                    MaxRecordsPerCompany = options.Gdelt.MaxRecordsPerCompany,
                    EnglishOnly = options.Gdelt.EnglishOnly,
                    InterRequestDelay = TimeSpan.FromSeconds(options.Gdelt.InterRequestDelaySeconds),
                    MaxRetriesOn429 = options.Gdelt.MaxRetriesOn429,
                    RetryBackoff = TimeSpan.FromSeconds(options.Gdelt.RetryBackoffSeconds),
                });
            }
            else if (string.Equals(kind, "newssearch", StringComparison.OrdinalIgnoreCase))
            {
                services.AddNewsAttentionCollector(new NewsCollectorOptions
                {
                    MaxRecordsPerCompany = options.News.MaxRecordsPerCompany,
                    EnglishOnly = options.News.EnglishOnly,
                    InterRequestDelay = TimeSpan.FromSeconds(options.News.InterRequestDelaySeconds),
                });
            }
            else
            {
                throw new InvalidOperationException(
                    $"Radar:Collectors kind '{kind}' is not supported; valid kinds are \"rss\", \"localfile\", \"sec\", \"secform4\", \"sec13dg\", \"usaspending\", \"news\", and \"newssearch\".");
            }
        }

        // Wire the AI chat-client seam ONLY when a provider is configured (opt-in gate). AI is not a collector,
        // so it is gated on Provider presence rather than the Collectors list. A blank Provider (the default)
        // leaves the graph byte-for-byte identical to today — no IChatClient/IChatClientFactory is registered.
        if (!string.IsNullOrWhiteSpace(options.Ai.Provider))
        {
            services.AddRadarAi(new AiClientOptions
            {
                Provider = options.Ai.Provider,
                Model = options.Ai.Model,
                AnthropicApiKey = options.Ai.Anthropic.ApiKey,
                OllamaEndpoint = options.Ai.Ollama.Endpoint,
            });

            // The filing analyzer rides the same opt-in gate: it consumes the IChatClient AddRadarAi just
            // registered, so it is only wired when a provider is configured. Blank Provider = neither runs.
            services.AddRadarFilingAnalyzer(new FilingAnalyzerOptions { MaxInputLength = options.Ai.MaxInputLength });

            // The directional filing signal source completes the arc: it composes the EX-99.1 earnings
            // reader + the filing analyzer into a confidence-gated directional GuidanceChange signal. Both
            // ride this same opt-in gate, so with a blank Provider none of them are registered and the
            // runner's optional IDirectionalFilingSignalSource stays null (default graph unchanged). The
            // reader only strictly needs the UserAgent, but the SEC options are passed consistently.
            services.AddSecEarningsReleaseReader(new SecCollectorOptions
            {
                UserAgent = options.Sec.UserAgent,
                Forms = options.Sec.Forms,
                MaxFilingsPerCompany = options.Sec.MaxFilingsPerCompany,
            });
            services.AddDirectionalFilingSignals(new DirectionalFilingSignalOptions
            {
                MinConfidence = options.Ai.MinConfidence,
                MaxFilingsPerRun = options.Ai.MaxFilingsPerRun,
            });
        }

        // Wire the price-history reference seam ONLY when Radar:Prices:Enabled is true (opt-in gate, mirroring the
        // Radar:Ai gate). Price is validation/reference data — NOT evidence, NOT a signal, NOT a scoring input
        // (AD-14): the reader is not an IEvidenceCollector, the store is consumed by nothing in the pipeline, and
        // the acquirer runs OUTSIDE IRadarPipeline. When disabled (the default) NONE of these are registered,
        // Worker's optional IPriceHistoryAcquirer? stays null, and the pipeline graph is byte-for-byte unchanged.
        if (options.Prices.Enabled)
        {
            if (options.Prices.InterRequestDelaySeconds < 0)
            {
                throw new InvalidOperationException(
                    "Radar:Prices:InterRequestDelaySeconds must not be negative; configure a non-negative polite "
                        + "pace (default 1) — a negative value is nonsensical configuration.");
            }

            // AddHttpPriceHistoryReader validates the range and fails fast on a typo'd Radar:Prices:Range.
            services.AddHttpPriceHistoryReader(options.Prices.Range);
            services.AddFilePriceHistoryStore(options.PricesDirectory);
            services.AddSingleton(new PriceAcquisitionOptions
            {
                InterRequestDelay = TimeSpan.FromSeconds(options.Prices.InterRequestDelaySeconds),
            });
            // TimeProvider.System is already registered by AddRadarApplicationServices (called above).
            services.AddSingleton<IPriceHistoryAcquirer, PriceHistoryAcquirer>();
        }

        // Wire the price-efficacy reporting seam ONLY when Radar:Efficacy:Enabled is true (opt-in gate, mirroring
        // the Radar:Prices gate). The efficacy layer is READ-ONLY over score history + price (AD-14 read side): it
        // JOINs persisted score snapshots to the price reference store and writes a per-company score-vs-price
        // SVG + CSV; it never feeds evidence/signal/scoring and runs OUTSIDE IRadarPipeline. When disabled (the
        // default) NONE of these are registered, Worker's optional IEfficacyReportGenerator? stays null, and the
        // pipeline graph is byte-for-byte unchanged.
        if (options.Efficacy.Enabled)
        {
            // The efficacy JOIN READS the price reference store. When price ACQUISITION is disabled the store is
            // not registered by the block above, so register the read-only file store here (pointing at the same
            // data/prices root) so the builder can read any existing {ticker}.json. When Prices.Enabled it is
            // already registered — avoid a duplicate registration.
            if (!options.Prices.Enabled)
            {
                services.AddFilePriceHistoryStore(options.PricesDirectory);
            }

            services.AddFileEfficacyArtifactStore(options.EfficacyDirectory);
            services.AddRadarEfficacyReport();
        }

        services.AddLocalFileCompanySeed(options.CompanySeedFilePath);
        services.AddFileRawEvidenceStore(options.EvidenceRawDirectory);
        services.AddFileSignalStore(options.SignalsDirectory);
        services.AddFileScoreStore(options.ScoresDirectory);
        services.AddFileReportWriter(options.ReportDirectory);
        services.AddFilePipelineRunStore(options.RunsDirectory);
        services.AddFileScoringConfigStore(options.ScoringConfigsDirectory);
        services.AddRadarPipeline();

        services.AddHostedService<Worker>();
        return services;
    }
}
