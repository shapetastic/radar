using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Sec;

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
                "Radar:Collectors must enable at least one collector; valid kinds are \"rss\", \"localfile\", and \"sec\".");
        }

        var seenKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawKind in options.Collectors)
        {
            // Validate/normalize first so a null/empty/whitespace entry fails fast with a clear
            // message instead of falling through to the "unknown kind" branch as "kind '' ...".
            if (string.IsNullOrWhiteSpace(rawKind))
            {
                throw new InvalidOperationException(
                    "Radar:Collectors entries must not be null, empty, or whitespace; valid kinds are \"rss\", \"localfile\", and \"sec\".");
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
            else
            {
                throw new InvalidOperationException(
                    $"Radar:Collectors kind '{kind}' is not supported; valid kinds are \"rss\", \"localfile\", and \"sec\".");
            }
        }

        services.AddLocalFileCompanySeed(options.CompanySeedFilePath);
        services.AddFileRawEvidenceStore(options.EvidenceRawDirectory);
        services.AddFileSignalStore(options.SignalsDirectory);
        services.AddFileScoreStore(options.ScoresDirectory);
        services.AddFileReportWriter(options.ReportDirectory);
        services.AddRadarPipeline();

        services.AddHostedService<Worker>();
        return services;
    }
}
