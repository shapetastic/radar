using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Gdelt;
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

        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();

        // Enable the configured evidence collectors additively (case-insensitive). Each kind registers
        // its collector as IEvidenceCollector, composing into the IEnumerable the runner now consumes.
        // Fail fast with a clear message on an empty list or an unknown kind, mirroring the interval
        // check above. De-dupe defensively so a config typo listing the same kind twice registers once.
        if (options.Collectors is null || options.Collectors.Count == 0)
        {
            throw new InvalidOperationException(
                "Radar:Collectors must enable at least one collector; valid kinds are \"rss\", \"localfile\", \"sec\", \"usaspending\", and \"news\".");
        }

        var seenKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawKind in options.Collectors)
        {
            // Validate/normalize first so a null/empty/whitespace entry fails fast with a clear
            // message instead of falling through to the "unknown kind" branch as "kind '' ...".
            if (string.IsNullOrWhiteSpace(rawKind))
            {
                throw new InvalidOperationException(
                    "Radar:Collectors entries must not be null, empty, or whitespace; valid kinds are \"rss\", \"localfile\", \"sec\", \"usaspending\", and \"news\".");
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
            else
            {
                throw new InvalidOperationException(
                    $"Radar:Collectors kind '{kind}' is not supported; valid kinds are \"rss\", \"localfile\", \"sec\", \"usaspending\", and \"news\".");
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
        }

        services.AddLocalFileCompanySeed(options.CompanySeedFilePath);
        services.AddFileRawEvidenceStore(options.EvidenceRawDirectory);
        services.AddFileSignalStore(options.SignalsDirectory);
        services.AddFileScoreStore(options.ScoresDirectory);
        services.AddFileReportWriter(options.ReportDirectory);
        services.AddFilePipelineRunStore(options.RunsDirectory);
        services.AddRadarPipeline();

        services.AddHostedService<Worker>();
        return services;
    }
}
