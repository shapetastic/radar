using System.Globalization;

using Radar.Application.Collectors;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Pipeline;
using Radar.Application.Prices;
using Radar.Application.Replay;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Infrastructure.Ai;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Fda;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Gdelt;
using Radar.Infrastructure.Hiring;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Patents;
using Radar.Infrastructure.Sec;
using Radar.Infrastructure.Trademarks;
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

        // Which PASS this process runs (spec 144), resolved here in the composition root — verb parsing is a
        // hosting concern and Radar.Application never sees a mode string. Reconciles Radar:RunMode with the
        // spec-139 Radar:Replay:Enabled switch and fails fast on an unknown mode or on the one contradictory
        // combination (a live collect/score pass asked for alongside a read-only replay).
        var runMode = RadarRunModes.Resolve(options.RunMode, options.Replay.Enabled);

        // Fail fast with a clear message: a non-positive interval would otherwise throw an opaque
        // ArgumentOutOfRangeException from PeriodicTimer when the worker starts looping.
        if (!options.RunOnce && options.IntervalMinutes <= 0)
        {
            throw new InvalidOperationException(
                $"Radar:IntervalMinutes must be greater than zero when Radar:RunOnce is false (was {options.IntervalMinutes}).");
        }

        // Resolve Radar:Collectors ONCE, through the ONE kind→collector table (spec 147), for BOTH the
        // vocabulary and the registrations below. Score mode is the only mode that does not require at least
        // one collector — there is nothing to collect with — but every other rule (unknown kind, blank entry,
        // case-insensitive matching, defensive de-dupe) applies identically in every mode: a silently-wrong
        // vocabulary would corrupt recorded provenance and could make the spec-146 channel guard reject a
        // legitimately-named collector.
        var configuredCollectors = ResolveConfiguredCollectors(
            options, requireAtLeastOne: runMode != RadarRunMode.Score);

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

        // The enabled-collector VOCABULARY (spec 147) — the collector NAMES and nothing else. Registered in
        // EVERY run mode, and BEFORE AddRadarApplicationServices so the library's TryAddSingleton default
        // (which derives the names from the REGISTERED collectors) loses to configuration. This is what makes
        // a standalone score pass — which registers no collector at all, deliberately — record the truthful
        // collector set instead of an empty one, and lets a radar-formula-v9 collector-channel strategy start
        // and score in that mode. It holds strings: it cannot collect and references nothing that can.
        services.AddSingleton(
            EnabledCollectorVocabulary.FromNames(configuredCollectors.Select(c => c.CollectorName)));

        // …and the orthogonal fact: whether collection happened in THIS pass. Only the standalone score pass
        // is marked, so full/collect/replay provenance is byte-identical to before spec 147. Replay is
        // deliberately NOT marked: it registers real collectors, and spec 139's replay ⊆ forward invariant
        // compares a replay snapshot against a forward one FIELD FOR FIELD — changing replay's provenance
        // string would break it.
        if (runMode == RadarRunMode.Score)
        {
            services.AddSingleton(new CollectionPassOptions
            {
                Kind = CollectionPassKind.NoCollectionThisPass,
            });
        }

        services.AddSingleton(new WorkerRunOptions
        {
            RunOnce = options.RunOnce,
            Interval = TimeSpan.FromMinutes(options.IntervalMinutes),
            Mode = runMode,
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

        // Same-event media-attention collapse window (spec 109): resolve Radar:Scoring:MediaCollapse and
        // register the concrete MediaCollapseOptions BEFORE AddRadarApplicationServices so configuration wins
        // over the library default (its TryAddSingleton is a no-op once this concrete instance is registered).
        // A blank/absent section binds the code default (3-day window). Fails fast on a non-positive window.
        services.AddRadarMediaCollapse(configuration);

        // Collector attribution (spec 151): resolve Radar:Scoring:InferLegacyCollectorAttribution and register
        // the concrete CollectorAttributionOptions + ICollectorAttributionResolver BEFORE
        // AddRadarApplicationServices so configuration wins over the library defaults (their TryAddSingletons
        // are no-ops once these concrete instances are registered). Absent/false — the shipped default — binds
        // the recorded-only resolver, which is behaviourally identical to pre-151: no score moves, no
        // provenance string changes, no fingerprint moves. Fails fast on an unparseable value.
        services.AddRadarCollectorAttribution(configuration);

        // Scoring strategies (spec 137): resolve Radar:Strategies / Radar:PrimaryStrategy and register the
        // concrete ScoringStrategySet BEFORE AddRadarApplicationServices so configuration wins over the
        // library default (its TryAddSingleton is a no-op once this concrete instance is registered). An
        // absent/empty Radar:Strategies synthesises the single "default" strategy from the ambient
        // Radar:Scoring:Profile — byte-identical to the single-engine composition, with the pinned default
        // fingerprints unmoved. Fails fast on an unknown profile, a blank/duplicate strategy name, or a
        // missing/unknown Radar:PrimaryStrategy.
        services.AddRadarScoringStrategies(configuration);

        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();

        // Global SEC request throttle: bind Radar:Sec:GlobalMinIntervalMs and register the concrete
        // SecRateLimitOptions so it wins over the Infrastructure TryAddSingleton default. One shared
        // SecRequestPacer (registered by each SEC Add* helper) then spaces EVERY *.sec.gov request — across all
        // collectors and the earnings reader — so the aggregate run rate stays under SEC's per-IP fair-access
        // ceiling (the unpaced collector burst is what blocks www.sec.gov). Fail fast on a negative interval even
        // for a run with no SEC client enabled, so a misconfig surfaces at startup rather than at first fetch.
        if (options.Sec.GlobalMinIntervalMs < 0)
        {
            throw new InvalidOperationException(
                "Radar:Sec:GlobalMinIntervalMs must not be negative; configure a non-negative pace in milliseconds "
                    + "(default 150, ~6.7 req/s) — set 0 to disable global SEC pacing. A negative value is nonsensical configuration.");
        }

        if (options.Sec.GlobalFetchTimeoutSeconds < 0)
        {
            throw new InvalidOperationException(
                "Radar:Sec:GlobalFetchTimeoutSeconds must not be negative; configure a non-negative per-fetch budget "
                    + "in seconds (default 100) — set 0 to disable the per-fetch timeout. A negative value is nonsensical configuration.");
        }

        services.AddSingleton(new SecRateLimitOptions
        {
            MinInterval = TimeSpan.FromMilliseconds(options.Sec.GlobalMinIntervalMs),
            FetchTimeout = TimeSpan.FromSeconds(options.Sec.GlobalFetchTimeoutSeconds),
        });

        // Enable the configured evidence collectors additively — UNLESS this is a standalone "score" pass
        // (spec 144), which registers NO COLLECTOR AT ALL. Construction is what opens the typed HttpClients
        // (and, for the SEC kinds, what enforces the User-Agent), so "constructs and invokes no collector"
        // has to mean "is never registered", not "is registered but never called".
        //
        // SPEC 147: the capability is still withheld, but the VOCABULARY is not — Radar:Collectors is
        // resolved and validated above in every mode, and the resulting EnabledCollectorVocabulary is
        // registered in every mode. So a score pass records the configured collector set (plus the
        // "collection=none-this-pass" marker) rather than a false empty one, and a radar-formula-v9 strategy
        // declaring collector channels starts and scores in score mode. The spec-146 guard ("a channel may
        // only name an ENABLED collector") is unweakened — it now validates against that same vocabulary.
        if (runMode != RadarRunMode.Score)
        {
            foreach (var collector in configuredCollectors)
            {
                collector.Register(services, options);
            }
        }

        // Wire the AI chat-client seam ONLY when a provider is configured (opt-in gate). AI is not a collector,
        // so it is gated on Provider presence rather than the Collectors list. A blank Provider (the default)
        // leaves the graph byte-for-byte identical to today — no IChatClient/IChatClientFactory is registered.
        //
        // SPEC 144: this block runs in EVERY mode, INCLUDING "score", and that is deliberate.
        // IDirectionalFilingSignalSource.ScoringDescriptor() is folded into ScoringConfigVersion via
        // SignalSourceDescriptor's ai= segment (spec 106/119), so omitting the seam from a score pass would
        // move the fingerprint and break "collect-then-score is byte-identical to the combined run". The
        // source is only ever INVOKED by the collection pass — which a score pass does not have — so "a score
        // pass performs no AI read" still holds structurally. Practical consequence: a score pass needs the
        // same Radar:Ai configuration (and the same API key in the environment) as the collect pass.
        AddConfiguredAi(services, options);

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

            // Spec 140: the strategy-vs-price comparison. Also READ-ONLY and downstream of scoring (AD-14) —
            // it re-runs the SAME join per strategy over the persisted score series and writes one extra
            // artifact pair. Enabled by default inside this already-opt-in gate: with too little joined
            // history it writes an honest "nothing could be ranked" leaderboard rather than failing, and it
            // never touches an existing artifact. The knobs are parsed and VALIDATED here, at the config
            // boundary, and cross into Radar.Application already resolved.
            if (options.Efficacy.Comparison.Enabled)
            {
                var comparisonOptions = BuildStrategyComparisonOptions(options.Efficacy.Comparison);
                var replayLabel = options.Efficacy.Comparison.ReplayLabel?.Trim();
                if (!string.IsNullOrEmpty(replayLabel))
                {
                    services.AddRadarStrategyComparisonOverReplay(
                        comparisonOptions, options.ReplayDirectory, replayLabel);
                }
                else
                {
                    services.AddRadarStrategyComparison(comparisonOptions);
                }
            }
        }

        // Wire the historical as-of replay seam ONLY when the resolved run mode is Replay (spec 139, extended
        // by spec 144's Radar:RunMode reconciliation — Radar:Replay:Enabled alone still selects it, unchanged).
        // Replay is a read-only OFFLINE mode: it re-scores ALREADY-STORED signals at past instants through the
        // SAME scoring seam the live pipeline uses, and writes only under Radar:ReplayDirectory. Otherwise NONE
        // of these are registered, Worker's optional IReplayRunner? stays null, and the pipeline graph is
        // byte-for-byte unchanged.
        //
        // The from/to/step series is parsed HERE, in the composition root, and crosses into Radar.Application
        // already resolved and validated — IConfiguration never reaches that layer (CLAUDE.md layering).
        if (runMode == RadarRunMode.Replay)
        {
            services.AddSingleton(BuildReplayPlan(options.Replay));
            services.AddRadarReplay(options.ReplayDirectory);
        }

        services.AddLocalFileCompanySeed(options.CompanySeedFilePath);
        services.AddFileRawEvidenceStore(options.EvidenceRawDirectory);
        services.AddFileSignalStore(options.SignalsDirectory);
        // Spec 142: the composed app reads accrued history. Repoints ISignalRepository /
        // IEvidenceRepository from the empty-every-process in-memory singletons registered by
        // AddInMemoryRadarPersistence onto the SAME file-store instances registered on the two lines above.
        // Must follow them (it resolves those singletons) and AddInMemoryRadarPersistence (it removes their
        // in-memory registrations). No config toggle: a composed run that could silently score from an empty
        // store is precisely the failure mode this slice exists to remove. It is ALSO what makes spec 144's
        // standalone score pass possible at all — without it that pass would score an empty store.
        services.AddDurableRadarSignalHistory();
        services.AddFileScoreStore(options.ScoresDirectory);
        services.AddFileReportWriter(options.ReportDirectory);
        services.AddFilePipelineRunStore(options.RunsDirectory);
        services.AddFileScoringConfigStore(options.ScoringConfigsDirectory);

        // Spec 144: which PASS this process runs. Replay keeps registering the combined pipeline exactly as
        // before — the replay runner REPLACES the run inside Worker, so the graph must stay byte-for-byte
        // what it was.
        switch (runMode)
        {
            case RadarRunMode.Collect:
                services.AddRadarCollectOnlyPipeline();
                break;
            case RadarRunMode.Score:
                // The as-of instant is parsed here (config→domain boundary) and crosses into
                // Radar.Application already resolved. Blank ⇒ null ⇒ "now" at run time.
                services.AddSingleton(new ScoringPassOptions
                {
                    AsOfUtc = string.IsNullOrWhiteSpace(options.Score.AsOfUtc)
                        ? null
                        : ParseUtcInstant(options.Score.AsOfUtc, "Radar:Score:AsOfUtc"),
                });
                services.AddRadarScoreOnlyPipeline();
                break;
            default:
                services.AddRadarPipeline();
                break;
        }

        services.AddHostedService<Worker>();
        return services;
    }

    /// <summary>
    /// ONE <c>Radar:Collectors</c> kind ↦ (provenance name, registration) entry. The name is taken from the
    /// collector class's own <c>Name</c> const, never re-typed, so the vocabulary a score pass records can
    /// never claim a name the registered collector does not have.
    /// </summary>
    private sealed record CollectorKind(
        string Kind, string CollectorName, Action<IServiceCollection, RadarWorkerOptions> Register);

    /// <summary>
    /// THE kind→collector table (spec 147). Both paths that need to know the collectors — the registration
    /// path (full/collect/replay) and the vocabulary path (every mode, including the standalone score pass
    /// that registers none) — resolve <c>Radar:Collectors</c> through this ONE table, so they cannot disagree.
    /// A vocabulary that drifted from the registrations would be worse than the failure it replaces: the
    /// spec-146 channel guard would pass on a collector that cannot run.
    /// <para>Order defines the order the kinds are listed in every fail-fast message.</para>
    /// </summary>
    private static readonly IReadOnlyList<CollectorKind> CollectorKinds =
    [
        new("rss", RadarCollectorNames.Rss,
            static (services, _) => services.AddRssPressReleaseCollector()),
        new("localfile", RadarCollectorNames.LocalFile,
            static (services, options) => services.AddLocalFileCollector(options.EvidenceSourceDirectory)),
        new("sec", RadarCollectorNames.SecEdgar,
            static (services, options) => services.AddSecEdgarCollector(new SecCollectorOptions
            {
                UserAgent = options.Sec.UserAgent,
                Forms = options.Sec.Forms,
                MaxFilingsPerCompany = options.Sec.MaxFilingsPerCompany,
            })),
        new("secform4", RadarCollectorNames.SecForm4,
            static (services, options) => services.AddSecForm4Collector(new SecForm4CollectorOptions
            {
                UserAgent = options.SecForm4.UserAgent,
                MaxFilingsPerCompany = options.SecForm4.MaxFilingsPerCompany,
            })),
        new("sec13dg", RadarCollectorNames.Sec13DG,
            static (services, options) => services.AddSec13DGCollector(new Sec13DGCollectorOptions
            {
                UserAgent = options.Sec13DG.UserAgent,
                MaxFilingsPerCompany = options.Sec13DG.MaxFilingsPerCompany,
            })),
        new("usaspending", RadarCollectorNames.UsaSpending,
            static (services, options) => services.AddUsaSpendingContractCollector(new UsaSpendingCollectorOptions
            {
                AwardTypeCodes = options.UsaSpending.AwardTypeCodes,
                LookbackDays = options.UsaSpending.LookbackDays,
                MaxAwardsPerCompany = options.UsaSpending.MaxAwardsPerCompany,
            })),
        new("news", RadarCollectorNames.GdeltNews,
            static (services, options) => services.AddGdeltNewsCollector(new GdeltCollectorOptions
            {
                Timespan = options.Gdelt.Timespan,
                MaxRecordsPerCompany = options.Gdelt.MaxRecordsPerCompany,
                EnglishOnly = options.Gdelt.EnglishOnly,
                InterRequestDelay = TimeSpan.FromSeconds(options.Gdelt.InterRequestDelaySeconds),
                MaxRetriesOn429 = options.Gdelt.MaxRetriesOn429,
                RetryBackoff = TimeSpan.FromSeconds(options.Gdelt.RetryBackoffSeconds),
            })),
        new("newssearch", RadarCollectorNames.NewsSearch,
            static (services, options) => services.AddNewsAttentionCollector(new NewsCollectorOptions
            {
                MaxRecordsPerCompany = options.News.MaxRecordsPerCompany,
                EnglishOnly = options.News.EnglishOnly,
                InterRequestDelay = TimeSpan.FromSeconds(options.News.InterRequestDelaySeconds),
            })),
        new("hiringats", RadarCollectorNames.HiringAts,
            static (services, options) => services.AddHiringBoardCollector(new HiringCollectorOptions
            {
                MaxSampleTitles = options.Hiring.MaxSampleTitles,
            })),
        new("patents", RadarCollectorNames.Patents,
            static (services, options) => services.AddPatentActivityCollector(new PatentCollectorOptions
            {
                BaseUrl = options.Patents.BaseUrl,
                LookbackDays = options.Patents.LookbackDays,
                MaxSampleTitles = options.Patents.MaxSampleTitles,
                ApiKeyEnvVar = options.Patents.ApiKeyEnvVar,
                MaxPageSize = options.Patents.MaxPageSize,
            })),
        new("fda", RadarCollectorNames.Fda,
            static (services, options) => services.AddFdaClearanceCollector(new FdaCollectorOptions
            {
                LookbackDays = options.Fda.LookbackDays,
                MaxSampleClearances = options.Fda.MaxSampleClearances,
                MaxPageSize = options.Fda.MaxPageSize,
            })),
        new("trademarks", RadarCollectorNames.Trademarks,
            static (services, options) => services.AddTrademarkActivityCollector(new TrademarkCollectorOptions
            {
                LookbackDays = options.Trademarks.LookbackDays,
                MaxSampleMarks = options.Trademarks.MaxSampleMarks,
                MaxPageSize = options.Trademarks.MaxPageSize,
                ApiKeyEnvVar = options.Trademarks.ApiKeyEnvVar,
            })),
    ];

    /// <summary>
    /// The valid-kind list every <c>Radar:Collectors</c> failure message quotes, rendered FROM
    /// <see cref="CollectorKinds"/> so a new collector cannot be added to the table and forgotten in the
    /// messages (asserted by a test).
    /// </summary>
    private static readonly string CollectorKindList = FormatCollectorKindList();

    /// <summary>
    /// The table's <c>(kind, collector name)</c> pairs, exposed for the spec-147 anti-drift test: it builds a
    /// provider for EVERY kind and asserts the registered <c>IEvidenceCollector.CollectorName</c> is the name
    /// recorded here, so the vocabulary can never claim a name the collector does not have.
    /// </summary>
    internal static IReadOnlyList<(string Kind, string CollectorName)> CollectorKindTable { get; } =
        CollectorKinds.Select(k => (k.Kind, k.CollectorName)).ToArray();

    /// <summary>
    /// The rendered valid-kind list quoted by every <c>Radar:Collectors</c> failure message, exposed so a
    /// test can assert it is derived from the table rather than hand-maintained alongside it.
    /// </summary>
    internal static string CollectorKindMessageList => CollectorKindList;

    private static string FormatCollectorKindList()
    {
        var quoted = CollectorKinds.Select(k => $"\"{k.Kind}\"").ToArray();
        return quoted.Length == 1
            ? quoted[0]
            : string.Join(", ", quoted[..^1]) + ", and " + quoted[^1];
    }

    /// <summary>
    /// Resolves <c>Radar:Collectors</c> against <see cref="CollectorKinds"/> (case-insensitive), preserving
    /// config order and de-duping defensively so a config typo listing the same kind twice resolves once.
    /// Fails fast with a clear message on an unknown kind or a null/empty/whitespace entry — in EVERY run
    /// mode, because the resolved list is also the recorded provenance VOCABULARY (spec 147) and a silently
    /// wrong one would misrepresent what produced a snapshot's data.
    /// <para>
    /// <paramref name="requireAtLeastOne"/> is the ONE mode-dependent rule: full/collect/replay must enable a
    /// collector (a run that collects nothing is a misconfiguration), while a standalone <c>score</c> pass
    /// has nothing to collect with and legitimately runs with none configured — it then records the empty
    /// vocabulary, which the <c>collection=none-this-pass</c> marker keeps unambiguous.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CollectorKind> ResolveConfiguredCollectors(
        RadarWorkerOptions options, bool requireAtLeastOne)
    {
        if (options.Collectors is null || options.Collectors.Count == 0)
        {
            if (!requireAtLeastOne)
            {
                return [];
            }

            throw new InvalidOperationException(
                $"Radar:Collectors must enable at least one collector; valid kinds are {CollectorKindList}.");
        }

        var seenKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<CollectorKind>(options.Collectors.Count);
        foreach (var rawKind in options.Collectors)
        {
            // Validate/normalize first so a null/empty/whitespace entry fails fast with a clear
            // message instead of falling through to the "unknown kind" branch as "kind '' ...".
            if (string.IsNullOrWhiteSpace(rawKind))
            {
                throw new InvalidOperationException(
                    "Radar:Collectors entries must not be null, empty, or whitespace; valid kinds are "
                        + $"{CollectorKindList}.");
            }

            var kind = rawKind.Trim();
            if (!seenKinds.Add(kind))
            {
                continue;
            }

            var match = CollectorKinds.FirstOrDefault(
                k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Radar:Collectors kind '{kind}' is not supported; valid kinds are {CollectorKindList}.");

            resolved.Add(match);
        }

        return resolved;
    }

    /// <summary>
    /// Wires the AI chat-client seam ONLY when a provider is configured (opt-in gate). AI is not a collector,
    /// so it is gated on Provider presence rather than the Collectors list. A blank Provider (the default)
    /// leaves the graph byte-for-byte identical to before AI existed — no IChatClient/IChatClientFactory is
    /// registered.
    /// <para>
    /// Spec 144: this runs in EVERY run mode, INCLUDING <c>score</c>, deliberately. The directional filing
    /// source's <c>ScoringDescriptor()</c> is a <c>ScoringConfigVersion</c> INPUT (via
    /// <c>SignalSourceDescriptor</c>'s <c>ai=</c> segment), so leaving it out of a score pass would move the
    /// fingerprint and break the byte-identical-scores guarantee. Only the collection pass ever INVOKES it.
    /// </para>
    /// </summary>
    private static void AddConfiguredAi(IServiceCollection services, RadarWorkerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Ai.Provider))
        {
            // For the OpenAI-compatible provider, an optional nested model (Radar:Ai:OpenAi:Model) overrides the
            // top-level Radar:Ai:Model; blank falls back to the top-level model so a single Ai.Model keeps working.
            var isOpenAiProvider = string.Equals(options.Ai.Provider.Trim(), "openai", StringComparison.OrdinalIgnoreCase);
            // Trimmed here (not just inside ChatClientFactory) so the model used for the SDK call, the
            // fingerprint descriptor and the analyzed-filing cache scope are all the same string.
            var effectiveModel = (isOpenAiProvider && !string.IsNullOrWhiteSpace(options.Ai.OpenAi.Model)
                ? options.Ai.OpenAi.Model
                : options.Ai.Model)?.Trim() ?? string.Empty;

            // Resolve the OpenAI-compatible API key from the env var NAMED by config (never from committed config,
            // mirroring the SEC-User-Agent secret precedent). Only the env-var NAME may appear in a message/log —
            // the key VALUE is never surfaced. Resolved only for the openai provider.
            string openAiApiKey = string.Empty;
            if (isOpenAiProvider)
            {
                var envVar = options.Ai.OpenAi.ApiKeyEnvVar?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(envVar))
                {
                    throw new InvalidOperationException(
                        "Radar:Ai:OpenAi:ApiKeyEnvVar must name the environment variable holding the OpenAI-compatible API key "
                            + "(e.g. \"DEEPINFRA_API_KEY\") when Provider is \"openai\" — the key is never committed to config.");
                }

                // Guard against a mis-paste: ApiKeyEnvVar must be the NAME of an environment variable, not the key
                // value. If it does not look like an env-var name, refuse WITHOUT echoing it — the messages below
                // interpolate envVar, so a pasted secret must never be allowed to reach an exception or a log.
                if (!IsLikelyEnvVarName(envVar))
                {
                    throw new InvalidOperationException(
                        "Radar:Ai:OpenAi:ApiKeyEnvVar must be the NAME of an environment variable "
                            + "(e.g. \"DEEPINFRA_API_KEY\"), not an API key value; the configured value is not a valid "
                            + "environment-variable name. Its value is not echoed here in case it is a secret.");
                }

                openAiApiKey = Environment.GetEnvironmentVariable(envVar) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(openAiApiKey))
                {
                    throw new InvalidOperationException(
                        $"Environment variable '{envVar}' (named by Radar:Ai:OpenAi:ApiKeyEnvVar) is not set or is empty; "
                            + "set it to the OpenAI-compatible host API key before selecting the \"openai\" provider. "
                            + "The key value is never logged.");
                }
            }

            services.AddRadarAi(new AiClientOptions
            {
                Provider = options.Ai.Provider,
                Model = effectiveModel,
                AnthropicApiKey = options.Ai.Anthropic.ApiKey,
                OllamaEndpoint = options.Ai.Ollama.Endpoint,
                OpenAiBaseUrl = options.Ai.OpenAi.BaseUrl,
                OpenAiApiKey = openAiApiKey,
            });

            // The filing analyzer rides the same opt-in gate: it consumes the IChatClient AddRadarAi just
            // registered, so it is only wired when a provider is configured. Blank Provider = neither runs.
            services.AddRadarFilingAnalyzer(new FilingAnalyzerOptions { MaxInputLength = options.Ai.MaxInputLength });

            // The directional filing signal source completes the arc: it composes the EX-99.1 earnings
            // reader + the filing analyzer into a confidence-gated directional GuidanceChange signal. Both
            // ride this same opt-in gate, so with a blank Provider none of them are registered and the
            // runner's optional IDirectionalFilingSignalSource stays null (default graph unchanged). The
            // reader only strictly needs the UserAgent, but the SEC options are passed consistently.
            services.AddSecEarningsReleaseReader(
                new SecCollectorOptions
                {
                    UserAgent = options.Sec.UserAgent,
                    Forms = options.Sec.Forms,
                    MaxFilingsPerCompany = options.Sec.MaxFilingsPerCompany,
                },
                new SecEarningsReleaseReaderOptions
                {
                    MaxRetriesOn429 = options.Sec.MaxRetriesOn429,
                    RetryBackoff = TimeSpan.FromSeconds(options.Sec.RetryBackoffSeconds),
                    MinRequestInterval = TimeSpan.FromMilliseconds(options.Sec.MinRequestIntervalMs),
                });
            // The single provider:model identity of the earnings reader, computed ONCE and used by BOTH
            // consumers below (the fingerprint descriptor and the analyzed-filing cache scope) so they can
            // never disagree about which model produced a run's directional reads.
            var aiModelIdentity = $"{options.Ai.Provider.Trim()}:{effectiveModel}";

            services.AddDirectionalFilingSignals(new DirectionalFilingSignalOptions
            {
                MinConfidence = options.Ai.MinConfidence,
                MaxFilingsPerRun = options.Ai.MaxFilingsPerRun,
                MaxConsecutiveRateLimited = options.Ai.MaxConsecutiveRateLimited,
                Strength = options.Ai.Strength,
                Novelty = options.Ai.Novelty,
                // Spec 119: folded into the scoring fingerprint by value — the reading model changes signal
                // DIRECTION, so two runs on different models must never share a ScoringConfigVersion.
                ModelIdentity = aiModelIdentity,
            });

            // Per-accession earnings-analysis-result cache (spec 107, AD-14 analogue): lets the directional
            // source replay a previously-analyzed filing instead of re-fetching the same www.sec.gov exhibit
            // every run. Rides the same opt-in AI gate (the source needs it at resolve time). The cache is
            // scoped to the analyzing provider:model identity (spec 118) so switching the earnings-read model
            // is a clean cache MISS (re-analyze) rather than a replay of another model's cached reads.
            services.AddFileAnalyzedFilingCache(options.AnalyzedFilingCacheDirectory, aiModelIdentity);

            // Opt-in AI filing-read debug store (spec 115, diagnostic-only / AD-14 read-side): persists what
            // each AI filing-read attempt saw and concluded, including no-signal and empty-body outcomes.
            // Default OFF — with PersistReadDebug false nothing is registered, the directional source's
            // optional IFilingReadDebugSink? stays null, and the graph is byte-for-byte unchanged.
            if (options.Ai.Filings.PersistReadDebug)
            {
                services.AddFileFilingReadDebugStore(options.FilingReadDebugDirectory);
            }
        }
    }

    /// <summary>
    /// Turns the bound <c>Radar:Efficacy:Comparison</c> numbers into validated
    /// <see cref="StrategyComparisonOptions"/> (spec 140) — the config→Application boundary, so
    /// <c>IConfiguration</c> never crosses into <c>Radar.Application</c>.
    /// <para>
    /// Every invalid value becomes a startup <b>fail-fast</b> naming the offending key. A leaderboard computed
    /// over a nonsensical horizon or a degenerate split would look exactly like a valid one, so the error has
    /// to arrive before anything is rendered.
    /// </para>
    /// </summary>
    private static StrategyComparisonOptions BuildStrategyComparisonOptions(
        StrategyComparisonWorkerOptions comparison)
    {
        try
        {
            return new StrategyComparisonOptions(
                comparison.ForwardHorizonDays,
                comparison.HoldOutFraction,
                comparison.MinimumObservations);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                "Radar:Efficacy:Comparison is misconfigured: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Turns the bound <c>Radar:Replay</c> strings into a validated <see cref="ReplayPlan"/> (spec 139).
    /// <para>
    /// This is the config→domain boundary: every parse failure becomes a startup <b>fail-fast</b> naming the
    /// offending key, because a replay that silently ran over the wrong range (or over an empty one) would
    /// produce a plausible-looking series that answers a different question than the operator asked.
    /// </para>
    /// </summary>
    private static ReplayPlan BuildReplayPlan(ReplayWorkerOptions replay)
    {
        var from = ParseReplayInstant(replay.From, "Radar:Replay:From");
        var to = ParseReplayInstant(replay.To, "Radar:Replay:To");
        var step = ParseReplayStep(replay.Step);

        ReplaySeries series;
        try
        {
            series = ReplaySeries.Create(from, to, step);
        }
        catch (ArgumentException ex)
        {
            // ReplaySeries owns the series rules (positive step, non-inverted range); re-throwing here only
            // adds the config keys that produced them, so the rule itself is never duplicated.
            throw new InvalidOperationException(
                "Radar:Replay:From/To/Step do not describe a usable as-of series: " + ex.Message, ex);
        }

        var label = string.IsNullOrWhiteSpace(replay.Label)
            ? DefaultReplayLabel(series)
            : replay.Label.Trim();

        try
        {
            return new ReplayPlan(label, series);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Radar:Replay:Label '{label}' is not usable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses one replay bound as a UTC instant. Blank is a startup failure here (a replay range is never
    /// inferred); the parse itself is the shared <see cref="ParseUtcInstant"/>.
    /// </summary>
    private static DateTimeOffset ParseReplayInstant(string? value, string configKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{configKey} is required when Radar:Replay:Enabled is true; supply a UTC date/time such as "
                    + "\"2026-05-01\" or \"2026-05-01T00:00:00Z\". A replay range is never inferred.");
        }

        return ParseUtcInstant(value, configKey);
    }

    /// <summary>
    /// Parses a configured as-of instant (a replay bound, or <c>Radar:Score:AsOfUtc</c>) as UTC. A value
    /// without an explicit offset is read as UTC (<c>AssumeUniversal</c>) rather than as machine-local time,
    /// so "2026-05-01" means the same as-of instant on every machine — an as-of instant's whole premise is a
    /// reproducible point in time (AD-7). ONE parser for every such key, so the two can never disagree about
    /// what an offsetless value means.
    /// </summary>
    private static DateTimeOffset ParseUtcInstant(string value, string configKey)
    {
        if (!DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"{configKey} is '{value}', which is not a date/time; supply a UTC date/time such as "
                    + "\"2026-05-01\" or \"2026-05-01T00:00:00Z\".");
        }

        return parsed;
    }

    /// <summary>
    /// Parses <c>Radar:Replay:Step</c>: a "{n}d" / "{n}h" / "{n}m" / "{n}s" token (the readable form an
    /// operator actually types) or any plain TimeSpan string. Tokens are tried FIRST because "1d" is not a
    /// TimeSpan at all while a bare "1" parses as one DAY — accepting only one of the two forms silently would
    /// be the difference between a 90-point and a 3-point replay.
    /// </summary>
    private static TimeSpan ParseReplayStep(string? value)
    {
        var raw = value?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            throw new InvalidOperationException(
                "Radar:Replay:Step is blank; supply a positive step such as \"1d\", \"12h\" or \"30m\".");
        }

        var unit = char.ToLowerInvariant(raw[^1]);
        if (unit is 'd' or 'h' or 'm' or 's'
            && int.TryParse(
                raw[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return unit switch
            {
                'd' => TimeSpan.FromDays(count),
                'h' => TimeSpan.FromHours(count),
                'm' => TimeSpan.FromMinutes(count),
                _ => TimeSpan.FromSeconds(count),
            };
        }

        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var timeSpan))
        {
            return timeSpan;
        }

        throw new InvalidOperationException(
            $"Radar:Replay:Step is '{value}', which is not a step; supply a \"{{n}}d\"/\"{{n}}h\"/\"{{n}}m\"/"
                + "\"{n}s\" token (e.g. \"1d\") or a plain TimeSpan string.");
    }

    /// <summary>
    /// The label a replay gets when the operator did not name one: a deterministic function of the series
    /// itself, so re-running the SAME range lands on the SAME output directory (idempotent) while a different
    /// range is visibly a different run. The step is rendered from the parsed TimeSpan rather than echoing the
    /// configured token, because a TimeSpan-formatted step ("01:00:00") contains ':' — which is not a legal
    /// storage-segment character.
    /// </summary>
    private static string DefaultReplayLabel(ReplaySeries series)
    {
        var step = series.Step;
        var stepToken = step.Ticks % TimeSpan.TicksPerDay == 0
            ? $"{step.Ticks / TimeSpan.TicksPerDay}d"
            : step.Ticks % TimeSpan.TicksPerHour == 0
                ? $"{step.Ticks / TimeSpan.TicksPerHour}h"
                : step.Ticks % TimeSpan.TicksPerMinute == 0
                    ? $"{step.Ticks / TimeSpan.TicksPerMinute}m"
                    : step.Ticks % TimeSpan.TicksPerSecond == 0
                        ? $"{step.Ticks / TimeSpan.TicksPerSecond}s"
                        // Sub-second steps are exotic but must still label UNIQUELY: rounding one to "0s"
                        // would make two different steps share an output directory.
                        : $"{step.Ticks}t";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{series.FromUtc:yyyyMMdd}-{series.ToUtc:yyyyMMdd}-{stepToken}");
    }

    // A configured Radar:Ai:OpenAi:ApiKeyEnvVar is treated as a real environment-variable NAME only if it matches
    // the POSIX shape: a leading letter or underscore, then letters/digits/underscores. Anything else (an API key
    // pasted in by mistake almost always carries other characters) is rejected without echoing the value, so a
    // secret never lands in an exception message or log line.
    private static bool IsLikelyEnvVarName(string value)
    {
        if (value.Length == 0 || (!char.IsAsciiLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return true;
    }
}
