using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Lifecycle;
using Radar.Application.Filings;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Reports;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Pipeline;

public sealed class RadarPipelineRunnerTests
{
    // Fixed clock used for every run; both the scoring window (30d) and report period (7d) end here.
    private static readonly DateTimeOffset FixedNow = new(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);

    // Evidence is observed inside both windows so its signal can score and surface in the report.
    private static readonly DateTimeOffset Observed = new(2026, 2, 6, 0, 0, 0, TimeSpan.Zero);

    // Minimal IAttentionSourceWeights for the real RadarScoreFormulaV8 (every publisher = full genuine
    // outlet). The pipeline tests exercise end-to-end orchestration, not the attention tiering math.
    private sealed class AllGenuineWeights : IAttentionSourceWeights
    {
        public AttentionSourceResolution Resolve(string? sourceName) =>
            AttentionSourceResolution.Unclassified(1.0, sourceName ?? string.Empty);
        public string CanonicalDescriptor() => "test-all-genuine";
    }

    // Minimal ISignalSourceDescriptor for the ScoringEngine (spec 95): a fixed descriptor; these pipeline
    // tests exercise end-to-end orchestration, not the signal-source fingerprint input.
    private sealed class StubSourceDescriptor : ISignalSourceDescriptor
    {
        public string CanonicalDescriptor() => "test-src-desc";

        public string CollectionProvenance() => "collectors=test;";

        public IReadOnlyList<string> EnabledCollectors() => ["test"];
    }

    // Minimal ICollectionHealthValidator (spec 98): returns a fixed report. Defaults to Empty (clean),
    // so the health check is a no-op for the existing runner tests; one test injects a warning to assert
    // the runner surfaces it into the PipelineRunRecord without touching any scoring counter.
    private sealed class StubCollectionHealthValidator(CollectionHealthReport? report = null)
        : ICollectionHealthValidator
    {
        private readonly CollectionHealthReport _report = report ?? CollectionHealthReport.Empty;

        public Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(_report);
    }

    private const string CompanyName = "Northwind Robotics";
    private const string RawText =
        "Northwind Robotics announced a major new customer win with a Fortune 100 partner today.";
    private const string Excerpt = "major new customer win";

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Wraps an evidence list as a <see cref="CollectionResult"/> with an empty summary, for tests
    /// that do not care about collection health.
    /// </summary>
    private static CollectionResult AsResult(IReadOnlyCollection<CollectedEvidence> items) =>
        new(items, CollectionSummary.Empty);

    /// <summary>A deterministic, in-test evidence collector returning a fixed result.</summary>
    private sealed class FakeEvidenceCollector(CollectionResult result) : IEvidenceCollector
    {
        public FakeEvidenceCollector(IReadOnlyCollection<CollectedEvidence> items)
            : this(AsResult(items))
        {
        }

        public string CollectorName => "FakeEvidenceCollector";

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(
            CollectionContext context, CancellationToken ct) =>
            Task.FromResult(result);
    }

    /// <summary>
    /// A deterministic clock whose <see cref="GetUtcNow"/> advances by a fixed step on every call, so
    /// instants captured later in the run are strictly greater than instants captured earlier. Returns
    /// zero-offset values (the report builder requires zero offset).
    /// </summary>
    private sealed class AdvancingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow()
        {
            var n = Interlocked.Increment(ref _ticks) - 1;
            return start + TimeSpan.FromTicks(step.Ticks * n);
        }
    }

    /// <summary>
    /// A collector that stamps each returned collected-evidence's <see cref="CollectedEvidence.CollectedAt"/>
    /// from the injected clock at collection time, mirroring the production collector. With the
    /// advancing clock this makes collection time strictly precede the post-collection asOfUtc.
    /// </summary>
    private sealed class ClockStampingCollector(TimeProvider clock, CollectedEvidence template) : IEvidenceCollector
    {
        public string CollectorName => "ClockStampingCollector";

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(
            CollectionContext context, CancellationToken ct)
        {
            var stamped = template with { CollectedAt = clock.GetUtcNow() };
            return Task.FromResult(AsResult([stamped]));
        }
    }

    /// <summary>
    /// A configurable in-test collector with a caller-supplied <see cref="CollectorName"/>,
    /// <see cref="SourceType"/>, and fixed result. Records whether it was invoked so the multi-collector
    /// test can assert every registered collector ran.
    /// </summary>
    private sealed class ConfigurableCollector(
        string name, EvidenceSourceType type, CollectionResult result) : IEvidenceCollector
    {
        public bool WasCalled => CallCount > 0;

        /// <summary>How many times this collector ran — must stay 1 per run, spec 137.</summary>
        public int CallCount { get; private set; }

        public string CollectorName => name;

        public EvidenceSourceType SourceType => type;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// A deterministic, in-test extractor returning a fixed output for ANY evidence id. The runner now
    /// maps <see cref="CollectedEvidence"/> to evidence via the real mapper (which assigns a fresh id),
    /// so the extractor cannot key off a pre-chosen id — it returns the same output regardless.
    /// </summary>
    private sealed class AnyEvidenceSignalExtractor(ExtractSignalsOutput output) : ISignalExtractor
    {
        public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(output);
    }

    /// <summary>
    /// A fake <see cref="IRawEvidenceStore"/> that records every <see cref="EvidenceItem"/> it is asked
    /// to write and always reports a new write. Lets tests assert exactly which newly-stored evidence
    /// the runner mirrors to disk.
    /// </summary>
    private sealed class RecordingRawEvidenceStore : IRawEvidenceStore
    {
        public List<EvidenceItem> Written { get; } = new();

        public Task<bool> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct)
        {
            Written.Add(evidence);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// A fake <see cref="ISignalFileStore"/> that records every <c>(signal, review)</c> it is asked to
    /// write and returns a fixed path. Lets tests assert exactly which stored signals the runner
    /// mirrors to disk and that each recorded review traces back to its signal.
    /// </summary>
    private sealed class RecordingSignalFileStore : ISignalFileStore
    {
        public List<(Signal Signal, Radar.Domain.Signals.SignalReview Review)> Written { get; } = new();

        /// <summary>
        /// Spec 193 §1: when true the durable write DEGRADES — it returns
        /// <see cref="DurableWriteOutcome.Failed"/> without throwing, exactly as the real store does when
        /// <c>GracefulFileWriter</c> catches a disk failure. The in-process record is still kept (the
        /// production store keeps its in-memory index entry too), which is precisely the state that used to
        /// read as success.
        /// </summary>
        public bool FailWrites { get; set; }

        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct)
        {
            Written.Add((signal, review));
            return Task.FromResult(FailWrites
                ? DurableWriteResult.NotPersisted("written/signal.json")
                : DurableWriteResult.Succeeded("written/signal.json"));
        }

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<Signal> result = Written
                .Select(w => w.Signal)
                .Where(s => s.CompanyId == companyId)
                .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
                .Where(s => s.ObservedAtUtc > startExclusiveUtc && s.ObservedAtUtc <= endInclusiveUtc)
                .Where(s => s.CreatedAtUtc <= knownAsOfUtc)
                .OrderBy(s => s.ObservedAtUtc).ThenBy(s => s.Id)
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// A fake <see cref="IScoreSnapshotFileStore"/> that records every <c>(snapshot, links)</c> it is
    /// asked to write and returns a fixed path. Lets tests assert exactly which scored companies the
    /// runner mirrors to disk and that each recorded link traces back to its snapshot.
    /// </summary>
    private sealed class RecordingScoreSnapshotFileStore : IScoreSnapshotFileStore
    {
        public List<(Radar.Domain.Scoring.CompanyScoreSnapshot Snapshot,
            IReadOnlyList<Radar.Domain.Scoring.ScoreEvidenceLink> Links)> Written { get; } = new();

        /// <summary>Spec 193 §1: see <see cref="RecordingSignalFileStore.FailWrites"/>.</summary>
        public bool FailWrites { get; set; }

        public Task<DurableWriteResult> WriteAsync(
            Radar.Domain.Scoring.CompanyScoreSnapshot snapshot,
            IReadOnlyList<Radar.Domain.Scoring.ScoreEvidenceLink> links,
            CancellationToken ct)
        {
            Written.Add((snapshot, links));
            return Task.FromResult(FailWrites
                ? DurableWriteResult.NotPersisted("written/score.json")
                : DurableWriteResult.Succeeded("written/score.json"));
        }

        public Task<Radar.Domain.Scoring.CompanyScoreSnapshot?> ReadLatestBeforeAsync(
            Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
            Task.FromResult(Written
                .Select(w => w.Snapshot)
                .Where(s => s.CompanyId == companyId && s.CreatedAtUtc < beforeUtc)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ThenByDescending(s => s.Id)
                .FirstOrDefault());

        public Task<IReadOnlyList<Radar.Domain.Scoring.CompanyScoreSnapshot>> ReadAllForCompanyAsync(
            Guid companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Radar.Domain.Scoring.CompanyScoreSnapshot>>(Written
                .Select(w => w.Snapshot)
                .Where(s => s.CompanyId == companyId)
                .OrderBy(s => s.CreatedAtUtc)
                .ThenBy(s => s.Id)
                .ToList());
    }

    /// <summary>
    /// A fake <see cref="IReportFileWriter"/> that records every <see cref="RadarReport"/> it is asked
    /// to write and returns a fixed path. Lets tests assert whether (and which) report the runner
    /// wrote to disk.
    /// </summary>
    private sealed class RecordingReportFileWriter : IReportFileWriter
    {
        public List<RadarReport> Written { get; } = new();

        /// <summary>Spec 201 §1: when set, every write reports <see cref="DurableWriteOutcome.Failed"/>.</summary>
        public bool FailWrites { get; set; }

        public Task<DurableWriteResult> WriteAsync(RadarReport report, CancellationToken ct)
        {
            Written.Add(report);
            return Task.FromResult(DurableWriteResult.From("written/path.md", !FailWrites));
        }
    }

    /// <summary>
    /// A fake <see cref="IPipelineRunStore"/> that records every <see cref="PipelineRunRecord"/> it is
    /// asked to write and returns a fixed path. Lets tests assert the runner writes exactly one run
    /// record per run with the run's counts and ordered collector names.
    /// </summary>
    private sealed class RecordingPipelineRunStore : IPipelineRunStore
    {
        public List<PipelineRunRecord> Written { get; } = new();

        /// <summary>Spec 201 §1: when set, every write reports <see cref="DurableWriteOutcome.Failed"/>.</summary>
        public bool FailWrites { get; set; }

        public Task<DurableWriteResult> WriteAsync(PipelineRunRecord record, CancellationToken ct)
        {
            Written.Add(record);
            return Task.FromResult(DurableWriteResult.From("written/run.json", !FailWrites));
        }

        public Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct)
        {
            IReadOnlyList<PipelineRunRecord> recent = Written
                .OrderByDescending(r => r.CreatedAtUtc)
                .ThenByDescending(r => r.Id)
                .Take(Math.Max(count, 0))
                .ToList();
            return Task.FromResult(recent);
        }

        // Spec 169's time-bounded read: inclusive bounds, ascending CreatedAtUtc then Id (AD-3).
        public Task<IReadOnlyList<PipelineRunRecord>> ReadBetweenAsync(
            DateTimeOffset startInclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct)
        {
            IReadOnlyList<PipelineRunRecord> between = Written
                .Where(r => r.CreatedAtUtc >= startInclusiveUtc && r.CreatedAtUtc <= endInclusiveUtc)
                .OrderBy(r => r.CreatedAtUtc)
                .ThenBy(r => r.Id)
                .ToList();
            return Task.FromResult(between);
        }
    }

    /// <summary>
    /// A fake <see cref="IScoringConfigStore"/> mirroring the real store's best-effort, non-throwing
    /// contract (it never throws — the real store swallows disk errors via GracefulFileWriter). Counts
    /// every <see cref="WriteIfNewAsync"/> call and records the configs written, so tests can assert the
    /// runner writes the effective config exactly once per run (not once per company).
    /// </summary>
    private sealed class RecordingScoringConfigStore : IScoringConfigStore
    {
        public int WriteCallCount { get; private set; }

        public List<EffectiveScoringConfig> Written { get; } = new();

        /// <summary>
        /// The per-strategy-name fingerprint records the spec-141 tripwire reads/writes. Pre-seed an entry to
        /// simulate "this name was recorded on a previous run".
        /// </summary>
        public Dictionary<string, string> StrategyFingerprints { get; } = new(StringComparer.Ordinal);

        /// <summary>Spec 201 §1: when set, every config write reports <see cref="DurableWriteOutcome.Failed"/>.</summary>
        public bool FailWrites { get; set; }

        public Task<DurableWriteResult> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct)
        {
            WriteCallCount++;
            Written.Add(config);
            return Task.FromResult(
                DurableWriteResult.From($"written/scoring-configs/{config.Fingerprint}.json", !FailWrites));
        }

        public Task<string?> ReadStrategyFingerprintAsync(string strategyName, CancellationToken ct) =>
            Task.FromResult(StrategyFingerprints.GetValueOrDefault(strategyName));

        public Task<DurableWriteResult> RecordStrategyFingerprintAsync(
            string strategyName, string fingerprint, CancellationToken ct)
        {
            StrategyFingerprints[strategyName] = fingerprint;
            return Task.FromResult(
                DurableWriteResult.Succeeded($"written/scoring-configs/strategies/{strategyName}.json"));
        }
    }

    /// <summary>
    /// A test <see cref="IScoreSnapshotFileStoreFactory"/> (spec 137) that hands the PRIMARY strategy the
    /// harness's existing <see cref="RecordingScoreSnapshotFileStore"/> — so every pre-existing assertion over
    /// the "legacy path" store keeps meaning exactly what it meant — and every non-primary strategy its own
    /// recording store, addressable by strategy name.
    /// </summary>
    private sealed class RecordingScoreSnapshotFileStoreFactory(RecordingScoreSnapshotFileStore primary)
        : IScoreSnapshotFileStoreFactory
    {
        private readonly Dictionary<string, RecordingScoreSnapshotFileStore> _byStrategy =
            new(StringComparer.OrdinalIgnoreCase);

        public RecordingScoreSnapshotFileStore Primary { get; } = primary;

        public RecordingScoreSnapshotFileStore For(string strategyName) => _byStrategy[strategyName];

        public IScoreSnapshotFileStore ForStrategy(ScoringStrategyDefinition strategy)
        {
            if (strategy.IsPrimary)
            {
                return Primary;
            }

            if (!_byStrategy.TryGetValue(strategy.Name, out var store))
            {
                store = new RecordingScoreSnapshotFileStore();
                _byStrategy[strategy.Name] = store;
            }

            return store;
        }
    }

    /// <summary>
    /// Captures every log entry so a test can assert on the run log itself — spec 193 §1 makes the
    /// aggregated per-store Warning and the summary shortfall statement part of the contract.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Harness
    {
        public InMemoryEvidenceRepository Evidence { get; } = new();
        public RecordingRawEvidenceStore RawStore { get; } = new();
        public RecordingReportFileWriter ReportWriter { get; } = new();
        public RecordingPipelineRunStore RunStore { get; } = new();
        public RecordingScoringConfigStore ScoringConfigStore { get; }
        public InMemoryCompanyRepository Companies { get; } = new();
        public InMemorySignalRepository Signals { get; } = new();
        public InMemorySignalReviewRepository Reviews { get; } = new();
        public RecordingSignalFileStore SignalStore { get; } = new();
        public RecordingScoreSnapshotFileStore ScoreStore { get; } = new();
        public InMemoryScoreRepository Scores { get; } = new();
        public InMemoryReportRepository Reports { get; } = new();
        public RecordingScoreSnapshotFileStoreFactory ScoreStores { get; }
        public StrategyScopedScoreRepositoryFactory ScoreRepositories { get; }
        public ScoringStrategySet StrategySet { get; }
        public RadarPipelineRunner Runner { get; }

        // Spec 193 §1: the run log is now an assertable artifact (the aggregated per-store Warning and the
        // summary shortfall statement), so every runner/pass in the harness logs into a capturing logger
        // instead of NullLogger. Nothing else about the harness changes.
        public CapturingLogger<RadarPipelineRunner> RunnerLog { get; } = new();
        public CapturingLogger<CollectionPass> CollectionPassLog { get; } = new();
        public CapturingLogger<ScoringPass> ScoringPassLog { get; } = new();
        public CapturingLogger<CollectOnlyPipelineRunner> CollectOnlyLog { get; } = new();
        public CapturingLogger<ScoreOnlyPipelineRunner> ScoreOnlyLog { get; } = new();

        // Spec 144: the two extracted passes and the two standalone verb runners over the SAME graph.
        public CollectionPass CollectionPass { get; }
        public ScoringPass ScoringPass { get; }
        public CollectOnlyPipelineRunner CollectOnlyRunner { get; }
        public IScoringStrategyFactory StrategyFactory { get; }
        public IWeeklyReportBuilder ReportBuilder { get; }
        public TimeProvider Clock { get; }

        public Harness(
            IEvidenceCollector collector,
            ISignalExtractor extractor,
            PipelineOptions options,
            TimeProvider? timeProvider = null,
            IDirectionalFilingSignalSource? directionalFilingSignals = null,
            ICollectionHealthValidator? healthValidator = null,
            ScoringStrategySet? strategies = null,
            RecordingScoringConfigStore? scoringConfigStore = null,
            ISignalSourceDescriptor? sourceDescriptor = null)
            : this(
                [collector], extractor, options, timeProvider, directionalFilingSignals, healthValidator,
                strategies, scoringConfigStore, sourceDescriptor)
        {
        }

        public Harness(
            IReadOnlyList<IEvidenceCollector> collectors,
            ISignalExtractor extractor,
            PipelineOptions options,
            TimeProvider? timeProvider = null,
            IDirectionalFilingSignalSource? directionalFilingSignals = null,
            ICollectionHealthValidator? healthValidator = null,
            ScoringStrategySet? strategies = null,
            // Spec 141: shareable across two harnesses so a test can simulate "a previous run recorded this
            // strategy's identity", which is what the startup tripwire compares against.
            RecordingScoringConfigStore? scoringConfigStore = null,
            // Spec 141: lets a test swap in the REAL SignalSourceDescriptor over a chosen collector set, to
            // prove a collector toggle does not trip that tripwire end-to-end.
            ISignalSourceDescriptor? sourceDescriptor = null)
        {
            ScoringConfigStore = scoringConfigStore ?? new RecordingScoringConfigStore();
            var time = timeProvider ?? new FixedTimeProvider(FixedNow);
            Clock = time;

            var resolver = new CompanyResolver(Companies, NullLogger<CompanyResolver>.Instance);
            var reviewer = new DeterministicSignalReviewer(
                time, NullLogger<DeterministicSignalReviewer>.Instance);
            var sourceWeights = new AllGenuineWeights();

            // Default composition == the single synthesised "default" primary strategy, i.e. exactly the
            // pre-spec-137 single-engine graph.
            StrategySet = strategies ?? ScoringStrategySet.SingleDefault(new ScoringWeights());
            ScoreStores = new RecordingScoreSnapshotFileStoreFactory(ScoreStore);
            ScoreRepositories = new StrategyScopedScoreRepositoryFactory(Scores);
            var strategyFactory = new ScoringStrategyFactory(
                StrategySet,
                Signals,
                SignalStore,
                Evidence,
                ScoreRepositories,
                Companies,
                new RadarScoreFormulaFactory(sourceWeights),
                sourceWeights,
                sourceDescriptor ?? new StubSourceDescriptor(),
                new InsiderMaterialityWeights(),
                new MediaAttentionCollapse(new MediaCollapseOptions()),
                new ScoringOptions(),
                NullLogger<ScoringEngine>.Instance);
            StrategyFactory = strategyFactory;
            var reportBuilder = new WeeklyReportBuilder(
                Companies,
                Scores,
                Evidence,
                Signals,
                Reviews,
                new WeeklyReportActionPolicyV1(),
                new MarkdownWeeklyReportRenderer(),
                Reports,
                RunStore,
                ScoreStore,
                // Spec 150: the report renders one plain ranked table per strategy when more than one is
                // configured, so it reads the same strategy set + per-strategy score repositories the
                // scoring pass writes through.
                strategyFactory,
                ScoreRepositories,
                // Spec 184: the per-strategy file-store factory (a non-primary LEAD's cross-run read
                // path) plus the INERT operating-call/evidence-fact sources — so these pipeline tests
                // keep exercising the pre-184 storage-primary narrative unchanged.
                ScoreStores,
                NullOperatingCallSource.Instance,
                UnavailableStrategyEvidenceFactsSource.Instance,
                new WeeklyReportOptions(),
                time,
                NullLogger<WeeklyReportBuilder>.Instance);
            ReportBuilder = reportBuilder;

            var mapper = new CollectedEvidenceMapper(
                new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance);

            // Spec 144: the runner is now the COMPOSITION of the two extracted passes. The harness builds
            // them from exactly the same fakes the runner used to hold directly, so every pre-existing
            // assertion still exercises the same code — only the seam moved.
            CollectionPass = new CollectionPass(
                collectors,
                mapper,
                Evidence,
                RawStore,
                extractor,
                resolver,
                reviewer,
                Signals,
                Reviews,
                SignalStore,
                Companies,
                healthValidator ?? new StubCollectionHealthValidator(),
                time,
                CollectionPassLog,
                new AllGenuineWeights(),
                directionalFilingSignals);

            ScoringPass = new ScoringPass(
                strategyFactory, ScoreStores, ScoringConfigStore, ScoringPassLog);

            Runner = new RadarPipelineRunner(
                CollectionPass,
                ScoringPass,
                strategyFactory,
                ScoringConfigStore,
                reportBuilder,
                ReportWriter,
                RunStore,
                options,
                RunnerLog);

            CollectOnlyRunner = new CollectOnlyPipelineRunner(
                CollectionPass,
                strategyFactory,
                ScoringConfigStore,
                RunStore,
                CollectOnlyLog);
        }

        /// <summary>
        /// Spec 161: the SAME collect-only runner, told which companies the pass was restricted to. The
        /// filter itself is applied at the seed source; here it is provenance only, so this differs from
        /// <see cref="CollectOnlyRunner"/> in exactly one constructor argument.
        /// </summary>
        public CollectOnlyPipelineRunner FilteredCollectOnlyRunner(CompanyFilter filter) =>
            new(
                CollectionPass,
                StrategyFactory,
                ScoringConfigStore,
                RunStore,
                CollectOnlyLog,
                filter);

        /// <summary>
        /// A standalone score runner over the SAME graph, at the supplied as-of instant (null ⇒ now from the
        /// harness clock). Built on demand so a test can choose the instant.
        /// </summary>
        public ScoreOnlyPipelineRunner ScoreOnlyRunner(
            PipelineOptions options, DateTimeOffset? asOfUtc = null, TimeProvider? timeProvider = null) =>
            new(
                ScoringPass,
                Companies,
                StrategyFactory,
                ScoringConfigStore,
                ReportBuilder,
                ReportWriter,
                RunStore,
                options,
                new ScoringPassOptions { AsOfUtc = asOfUtc },
                timeProvider ?? Clock,
                ScoreOnlyLog);
    }

    /// <summary>
    /// Builds a raw <see cref="CollectedEvidence"/> for the collector. The runner maps it to an
    /// <see cref="EvidenceItem"/> via the real mapper (which normalizes title+rawText into the content
    /// hash and assigns a fresh id). Dedup is therefore by normalized content, not by a pre-chosen id.
    /// </summary>
    private static CollectedEvidence BuildCollected(string rawText = RawText) =>
        new(
            SourceType: EvidenceSourceType.LocalFile,
            SourceName: "Northwind Newsroom",
            SourceUrl: "https://example.com/nw",
            Title: "Northwind Robotics customer win",
            RawText: rawText,
            PublishedAt: Observed,
            CollectedAt: FixedNow,
            Metadata: new Dictionary<string, string> { ["quality"] = "High" });

    private static ExtractedSignal MaterialSignal(
        string mention = CompanyName,
        string type = "CustomerWin",
        string excerpt = Excerpt) =>
        new(
            CompanyMention: mention,
            SignalType: type,
            Direction: "Positive",
            Strength: 4,
            Novelty: 4,
            Confidence: 0.8m,
            SupportingExcerpt: excerpt,
            Reason: "Material customer win reported by the company newsroom.");

    private async Task SeedCompanyAsync(Harness h, Guid companyId, string name = CompanyName)
    {
        var company = new CompanyBuilder()
            .WithId(companyId)
            .WithName(name)
            .WithTicker("NWR")
            .Build();
        await h.Companies.AddAsync(company, default);
    }

    [Fact]
    public void Constructor_WithNoCollectors_FailsFast()
    {
        // DI supplies an empty enumerable when no collector is registered; the runner must reject it
        // rather than "succeed" while silently collecting zero evidence.
        var extractor = new AnyEvidenceSignalExtractor(new([], "summary"));

        var ex = Assert.Throws<ArgumentException>(
            () => new Harness(Array.Empty<IEvidenceCollector>(), extractor, new PipelineOptions()));
        Assert.Equal("collectors", ex.ParamName);
    }

    // ---------------------------------------------------------------------------------------------
    // SPEC 193 §1 — a failed durable write must never read as success.
    //
    // Before this slice the file stores swallowed a disk failure, kept the in-memory copy and returned a
    // path, and CollectionPass explicitly commented that "the store swallows disk errors, so this must not
    // change any counter". The graceful degradation is kept (a disk hiccup must not crash a run, and the
    // current run still completes on what it has); only the CLAIM changed.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailedSignalWrite_RunCompletes_ButIsCountedAndAggregatedIntoExactlyOneWarning()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions());
        await SeedCompanyAsync(h, companyId);
        h.SignalStore.FailWrites = true;

        // The run COMPLETES — no throw, and every pre-existing counter is unchanged: the signal really was
        // extracted, validated and approved. What it was not is durably stored.
        var result = await h.Runner.RunAsync(default);
        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(1, result.SignalsApproved);
        Assert.Equal(1, result.CompaniesScored);

        // The in-memory read still returns the item, exactly as the production store keeps its index entry.
        var signal = Assert.Single(await h.Signals.GetByCompanyAsync(companyId, default));
        Assert.Equal(SignalReviewStatus.Approved, signal.ReviewStatus);

        // The run record counts it — and does not claim the snapshot side failed too.
        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(1, record.SignalsNotPersisted);
        Assert.Equal(0, record.ScoreSnapshotsNotPersisted);

        // EXACTLY ONE aggregated Warning for the store (spec 145's aggregation precedent), never one per
        // failure, and it says what the count means.
        var storeWarning = Assert.Single(h.CollectionPassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 signal(s)", storeWarning.Message);
        Assert.Contains("accrued signal history does NOT contain them", storeWarning.Message);

        // And the run says so in its summary — the run must not report the signal as durably stored.
        // A COMBINED run genuinely observed both axes, so both are rendered — including the measured zero.
        // Pinned as the full string because the wording is a stated byte-identical criterion, and because
        // omitting an observed 0 here is the most natural wrong generalisation of the null-axis rule.
        var summaryShortfall = Assert.Single(h.RunnerLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(
            "This run did NOT durably persist everything it produced: 1 signal(s) and 0 score snapshot(s) "
                + "exist only in this process's memory. The run completed and reported on them, but they are "
                + "absent from the accrued stores, so the next run's history read and the efficacy/replay "
                + "reads will not see them.",
            summaryShortfall.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 201 §1: the remaining durable writes a run performs — the weekly report, the per-strategy effective
    // scoring config and the run record itself — now report their outcome, and the runner CHECKS it.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailedReportWrite_IsCountedOnTheRunRecord_KeepsTheReportId_AndWarnsOnce()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);
        h.ReportWriter.FailWrites = true;

        var result = await h.Runner.RunAsync(default);

        // The report WAS generated: its id stays on the result and the record (the in-memory model may still
        // be re-rendered to the same path). What the record must NOT do is imply the file exists.
        Assert.NotNull(result.ReportId);
        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(result.ReportId, record.ReportId);
        Assert.Equal(1, record.ReportsNotPersisted);
        Assert.Equal(0, record.ScoringConfigsNotPersisted);

        var warning = Assert.Single(h.RunnerLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("weekly report could NOT be durably persisted", warning.Message);
        Assert.Contains("written/path.md", warning.Message);
    }

    [Fact]
    public async Task SuccessfulReportWrite_RecordsAMeasuredZero_NotNull()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, Guid.NewGuid());

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(0, record.ReportsNotPersisted);
        Assert.DoesNotContain(h.RunnerLog.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NoReportGenerated_LeavesTheReportCountNull_NeverAFabricatedZero()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, Guid.NewGuid());

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Null(record.ReportsNotPersisted);
        // The config write DID happen (one per strategy), so that axis is a measured zero.
        Assert.Equal(0, record.ScoringConfigsNotPersisted);
    }

    [Fact]
    public async Task FailedScoringConfigWrite_IsCountedPerStrategy_InOneAggregatedWarning()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var configStore = new RecordingScoringConfigStore { FailWrites = true };
        var h = new Harness(collector, extractor, new PipelineOptions(), scoringConfigStore: configStore);
        await SeedCompanyAsync(h, Guid.NewGuid());

        await h.Runner.RunAsync(default);

        // One strategy ⇒ one config write attempted ⇒ one not persisted. The snapshot side is untouched.
        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(1, record.ScoringConfigsNotPersisted);
        Assert.Equal(0, record.ScoreSnapshotsNotPersisted);

        var warning = Assert.Single(h.ScoringPassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 effective scoring config file(s)", warning.Message);
        Assert.Contains("dereferences to NOTHING on disk", warning.Message);
    }

    [Fact]
    public async Task CollectPass_LeavesReportAndScoringConfigCountsNull()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions());
        await SeedCompanyAsync(h, Guid.NewGuid());

        await h.CollectOnlyRunner.RunAsync(default);

        // A collect pass writes neither a report nor a scoring config: null, never a 0 claiming clean writes.
        var record = Assert.Single(h.RunStore.Written);
        Assert.Null(record.ReportsNotPersisted);
        Assert.Null(record.ScoringConfigsNotPersisted);
    }

    [Fact]
    public async Task FailedRunRecordWrite_IsWarnedOnce_AndTheRunStillCompletes()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions());
        await SeedCompanyAsync(h, Guid.NewGuid());
        h.RunStore.FailWrites = true;

        var result = await h.Runner.RunAsync(default);

        // The run completes on what it has; the ONE report of the lost run record is the runner's Warning
        // (the record cannot count its own failure on itself).
        Assert.Equal(1, result.CompaniesScored);
        var warning = Assert.Single(h.RunnerLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("pipeline run record could NOT be durably persisted", warning.Message);
        Assert.Contains("written/run.json", warning.Message);
    }

    [Fact]
    public async Task FailedScoreSnapshotWrite_IsCountedAcrossAllStrategies_InOneAggregatedWarning()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions());
        await SeedCompanyAsync(h, companyId);
        h.ScoreStore.FailWrites = true;

        var result = await h.Runner.RunAsync(default);

        // companiesScored keeps its established meaning: the company WAS scored (the snapshot exists in the
        // score repository the report reads). The disk is the separate axis.
        Assert.Equal(1, result.CompaniesScored);
        Assert.Single(await h.Scores.GetSnapshotsForCompanyAsync(companyId, default));

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(1, record.ScoreSnapshotsNotPersisted);
        Assert.Equal(0, record.SignalsNotPersisted);

        var storeWarning = Assert.Single(h.ScoringPassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 score snapshot(s)", storeWarning.Message);
        Assert.Contains("accrued score history does NOT contain them", storeWarning.Message);

        // The mirror of the signal-side pin: a combined run observed the signal axis too, so its measured 0
        // is rendered rather than dropped.
        var summaryShortfall = Assert.Single(h.RunnerLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("0 signal(s) and 1 score snapshot(s)", summaryShortfall.Message);
    }

    [Fact]
    public async Task ManyFailedWrites_StillProduceExactlyOneWarningPerStore()
    {
        // The aggregation is the point: a bad disk must not bury the run log in one line per failure.
        // Two distinct evidence items (each still carrying the excerpt, so each yields a real signal) and
        // two companies, so both stores are asked to write twice.
        var collector = new FakeEvidenceCollector([
            BuildCollected(RawText),
            BuildCollected(RawText + " A second, separately-collected report."),
        ]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions());
        await SeedCompanyAsync(h, Guid.NewGuid());
        await SeedCompanyAsync(h, Guid.NewGuid(), "Contoso Robotics");
        h.SignalStore.FailWrites = true;
        h.ScoreStore.FailWrites = true;

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(2, record.SignalsNotPersisted);
        Assert.Equal(2, record.ScoreSnapshotsNotPersisted);

        Assert.Single(h.CollectionPassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Single(h.ScoringPassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Single(h.RunnerLog.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HealthyRun_ReportsZeroNotPersisted_AndItsSummaryLineIsByteIdenticalToPre193()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(0, record.SignalsNotPersisted);
        Assert.Equal(0, record.ScoreSnapshotsNotPersisted);

        // No aggregated store Warning, no shortfall statement — and the existing summary line is
        // BYTE-IDENTICAL to the pre-193 text. This is the pinned criterion: the counts are appended as a
        // separate statement on the non-zero path only, never folded into this template.
        Assert.DoesNotContain(h.CollectionPassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(h.ScoringPassLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(h.RunnerLog.Entries, e => e.Level == LogLevel.Warning);

        var summary = Assert.Single(
            h.RunnerLog.Entries,
            e => e.Level == LogLevel.Information && e.Message.StartsWith("Pipeline run complete:", StringComparison.Ordinal));
        Assert.Equal(
            "Pipeline run complete: 1/1 new evidence, 1 approved / 0 needs-review signals, "
                + "1 companies scored by the primary of 1 strategies, 0/0 sources unreadable, report none.",
            summary.Message);
    }

    [Fact]
    public async Task CollectPass_RecordsItsSignalCount_ButLeavesTheSnapshotCountNull()
    {
        // A pass records only what it genuinely observed. A collect pass wrote no snapshot, so a 0 there
        // would claim a clean snapshot write that never happened — the same reason Strategies is null.
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions());
        await SeedCompanyAsync(h, Guid.NewGuid());
        h.SignalStore.FailWrites = true;

        await h.CollectOnlyRunner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(1, record.SignalsNotPersisted);
        Assert.Null(record.ScoreSnapshotsNotPersisted);

        var shortfall = Assert.Single(h.CollectOnlyLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("did NOT durably persist everything it produced", shortfall.Message);
        Assert.Contains("1 signal(s)", shortfall.Message);

        // ...and the summary line omits the snapshot axis ENTIRELY rather than rendering the null as "0
        // score snapshot(s)" — that would claim a clean snapshot write this pass never attempted, the exact
        // fabricated zero the nullable run-record counter above exists to avoid.
        Assert.DoesNotContain("score snapshot", shortfall.Message);
    }

    [Fact]
    public async Task ScorePass_RecordsItsSnapshotCount_ButLeavesTheSignalCountNull()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions());
        await SeedCompanyAsync(h, companyId);
        h.ScoreStore.FailWrites = true;

        await h.ScoreOnlyRunner(new PipelineOptions()).RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Null(record.SignalsNotPersisted);
        Assert.Equal(1, record.ScoreSnapshotsNotPersisted);

        var shortfall = Assert.Single(h.ScoreOnlyLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 score snapshot(s)", shortfall.Message);

        // Mirror of the collect-pass assertion: this pass observed no signal write, so the signal axis is
        // omitted rather than reported as a measured "0 signal(s)".
        Assert.DoesNotContain("signal(s)", shortfall.Message);
    }

    [Fact]
    public async Task HappyPath_FullChain_PersistsAndKeepsProvenance()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // Run-summary counts.
        Assert.Equal(1, result.EvidenceCollected);
        Assert.Equal(1, result.EvidenceNew);
        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(1, result.SignalsApproved);
        Assert.Equal(0, result.SignalsNeedingReview);
        Assert.Equal(1, result.CompaniesScored);
        Assert.NotNull(result.ReportId);

        // Exactly one signal persisted, resolved + approved. The mapper assigned the evidence id, so
        // discover it from the persisted signal and verify the evidence was persisted under it.
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(companyId, signal.CompanyId);
        Assert.Equal(SignalReviewStatus.Approved, signal.ReviewStatus);
        var evidenceId = signal.EvidenceId;
        Assert.NotNull(await h.Evidence.GetByIdAsync(evidenceId, default));

        // A snapshot exists for the company.
        var snapshots = await h.Scores.GetSnapshotsForCompanyAsync(companyId, default);
        var snapshot = Assert.Single(snapshots);

        // Report persisted, contains the company as a ranked entry.
        var report = await h.Reports.GetByIdAsync(result.ReportId!.Value, default);
        Assert.NotNull(report);
        var items = await h.Reports.GetItemsAsync(report!.Id, default);
        var item = Assert.Single(items);
        Assert.Equal(companyId, item.CompanyId);

        // Provenance: report item → snapshot → score-evidence link → persisted evidence.
        Assert.Equal(snapshot.Id, item.ScoreSnapshotId);
        var links = await h.Scores.GetLinksForSnapshotAsync(snapshot.Id, default);
        var link = Assert.Single(links);
        Assert.Equal(evidenceId, link.EvidenceId);
        Assert.Equal(signal.Id, link.SignalId);
    }

    [Fact]
    public async Task Run_PersistsOneSignalReviewPerStoredSignal_TracingToSignal()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        // One reviewed signal persisted; the audit trail carries exactly one review for it, and the
        // review's SignalId traces back to the stored signal (provenance).
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);

        var reviews = await h.Reviews.GetBySignalAsync(signal.Id, default);
        var review = Assert.Single(reviews);
        Assert.Equal(signal.Id, review.SignalId);
    }

    [Fact]
    public async Task Run_WithNoExtractedSignals_PersistsNoReviews()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);

        // Unknown type AND an excerpt absent from the evidence — the mapper drops the signal before
        // it is ever reviewed, so no SignalReview is produced or persisted.
        var invalid = MaterialSignal(type: "NotARealType", excerpt: "this text is absent from evidence");
        var extractor = new AnyEvidenceSignalExtractor(new([invalid], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(0, result.SignalsValid);

        // No signals were stored, so no reviews exist for any persisted signal.
        var signals = await h.Signals.GetObservedBetweenAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default);
        Assert.Empty(signals);
        Assert.Empty(await h.Reviews.GetBySignalAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task CollectionSummary_IsSurfacedIntoResult()
    {
        var companyId = Guid.NewGuid();

        // A collector whose run summary reports two checked sources, one of which failed.
        var summary = new CollectionSummary(
            SourcesChecked: 2,
            SourcesSucceeded: 1,
            SourcesFailed: 1,
            ItemsCollected: 1,
            Failures: [new SourceFailure("Broken Feed", "https://broken.test/rss", "HTTP 500")]);
        var collector = new FakeEvidenceCollector(new CollectionResult([BuildCollected()], summary));
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // The runner threads the collector's summary into the result. With a single collector the merge
        // rebuilds an equivalent CollectionSummary (concatenating the one result's evidence and
        // failures), so assert by value field-by-field rather than by reference.
        Assert.Equal(2, result.SourcesChecked);
        Assert.Equal(1, result.SourcesFailed);
        Assert.Equal(summary.SourcesChecked, result.Collection.SourcesChecked);
        Assert.Equal(summary.SourcesSucceeded, result.Collection.SourcesSucceeded);
        Assert.Equal(summary.SourcesFailed, result.Collection.SourcesFailed);
        Assert.Equal(summary.ItemsCollected, result.Collection.ItemsCollected);
        var failure = Assert.Single(result.Collection.Failures);
        Assert.Equal("Broken Feed", failure.SourceName);
    }

    [Fact]
    public async Task EvidenceCompanyHint_ResolvesSignalToHintedCompany()
    {
        var companyId = Guid.NewGuid();

        // Evidence carries the seeded company's ticker as a collector hint. The extracted signal's
        // mention is generic and would NOT resolve on its own — only the hint can resolve it.
        var collector = new FakeEvidenceCollector(
            [BuildCollected() with { CompanyHints = ["NWR"] }]);
        var extractor = new AnyEvidenceSignalExtractor(
            new([MaterialSignal(mention: "Some Generic Vendor Name")], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(1, result.SignalsApproved);

        // The runner threaded the hint to the resolver, so the signal resolved to the hinted company
        // and was approved.
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(companyId, signal.CompanyId);
        Assert.Equal(SignalReviewStatus.Approved, signal.ReviewStatus);
    }

    [Fact]
    public async Task UnresolvedMention_StaysConservative()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        // Empty company universe → mention cannot resolve.
        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(0, result.SignalsApproved);
        Assert.Equal(1, result.SignalsNeedingReview);
        Assert.Equal(0, result.CompaniesScored);

        // The persisted signal is unresolved and routed to human review.
        var observed = await h.Signals.GetObservedBetweenAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default);
        var signal = Assert.Single(observed);
        Assert.Null(signal.CompanyId);
        Assert.Equal(SignalReviewStatus.NeedsHumanReview, signal.ReviewStatus);
    }

    [Fact]
    public async Task RunningTwice_DoesNotDoubleStoreOrDoubleExtract()
    {
        var companyId = Guid.NewGuid();

        // The same CollectedEvidence maps to the same content hash each run (the mapper's normalizer is
        // deterministic over title+rawText), even though it assigns a fresh id every map. AddIfNewAsync
        // dedups by content hash, so the second run stores nothing new.
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);

        var first = await h.Runner.RunAsync(default);
        var signalsAfterFirst = await h.Signals.GetByCompanyAsync(companyId, default);

        var second = await h.Runner.RunAsync(default);
        var signalsAfterSecond = await h.Signals.GetByCompanyAsync(companyId, default);

        // First run stored the evidence + one signal.
        Assert.Equal(1, first.EvidenceNew);
        Assert.Single(signalsAfterFirst);

        // Second run: re-collected evidence is a duplicate (AddIfNewAsync false) and produces no
        // new signals. Counts of new evidence / valid signals drop to zero on the second pass.
        Assert.Equal(1, second.EvidenceCollected);
        Assert.Equal(0, second.EvidenceNew);
        Assert.Equal(0, second.SignalsExtracted);
        Assert.Equal(0, second.SignalsValid);
        Assert.Single(signalsAfterSecond);

        // A second scoring snapshot per company is expected and fine.
        var snapshots = await h.Scores.GetSnapshotsForCompanyAsync(companyId, default);
        Assert.Equal(2, snapshots.Count);
    }

    [Fact]
    public async Task RunningTwice_MirrorsOnlyNewlyStoredEvidenceToRawStore()
    {
        var companyId = Guid.NewGuid();

        // Same deterministic collected evidence both runs: the second run dedupes by content hash so
        // only the first run's newly-stored evidence is mirrored to the raw store.
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);
        await h.Runner.RunAsync(default);

        // Exactly one write: the re-collected duplicate on the second run is not re-written.
        var written = Assert.Single(h.RawStore.Written);

        // It matches the persisted evidence (same content hash and id discovered via the signal).
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        var persisted = await h.Evidence.GetByIdAsync(signal.EvidenceId, default);
        Assert.NotNull(persisted);
        Assert.Equal(persisted!.Id, written.Id);
        Assert.Equal(persisted.ContentHash, written.ContentHash);
    }

    [Fact]
    public async Task Run_MirrorsEachStoredSignalToFileStore_TracingReviewToSignal()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        // Exactly one signal stored, so exactly one signal-file write; the recorded review's
        // SignalId traces back to the stored signal (provenance holds on the on-disk mirror).
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);

        var write = Assert.Single(h.SignalStore.Written);
        Assert.Equal(signal.Id, write.Signal.Id);
        Assert.Equal(signal.Id, write.Review.SignalId);
    }

    [Fact]
    public async Task RunningTwice_MirrorsOnlyNewlyStoredSignalsToFileStore()
    {
        var companyId = Guid.NewGuid();

        // Same deterministic collected evidence both runs: the second run dedupes by content hash so
        // it produces no new signals, hence no extra signal-file writes.
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);
        await h.Runner.RunAsync(default);

        // Exactly one write: the re-collected duplicate on the second run yields no new signal.
        var write = Assert.Single(h.SignalStore.Written);

        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(signal.Id, write.Signal.Id);
        Assert.Equal(signal.Id, write.Review.SignalId);
    }

    [Fact]
    public async Task Run_MirrorsEachScoredCompanyToScoreFileStore_PreservingProvenance()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // Exactly one score-file write per scored company.
        Assert.Equal(result.CompaniesScored, h.ScoreStore.Written.Count);

        // Provenance preserved through the runner: every recorded link traces back to its snapshot.
        foreach (var write in h.ScoreStore.Written)
        {
            foreach (var link in write.Links)
            {
                Assert.Equal(write.Snapshot.Id, link.ScoreSnapshotId);
            }
        }
    }

    [Fact]
    public async Task InvalidExtractedSignal_IsDroppedNotPersisted()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);

        // Unknown type AND an excerpt not present in the evidence — both make the mapper reject it.
        var invalid = MaterialSignal(type: "NotARealType", excerpt: "this text is absent from evidence");
        var extractor = new AnyEvidenceSignalExtractor(new([invalid], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(0, result.SignalsValid);
        Assert.Equal(0, result.SignalsApproved);
        Assert.Equal(0, result.SignalsNeedingReview);

        // Nothing persisted for the invalid signal.
        var observed = await h.Signals.GetObservedBetweenAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default);
        Assert.Empty(observed);
    }

    [Fact]
    public async Task GenerateReportFalse_ProducesNoReport()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // No report id is returned when GenerateReport is false: Stage 7 was skipped.
        Assert.Null(result.ReportId);

        // Scoring (Stage 6) still ran: the company has exactly one snapshot.
        var snapshots = await h.Scores.GetSnapshotsForCompanyAsync(companyId, default);
        Assert.Single(snapshots);
    }

    [Fact]
    public async Task GenerateReportTrue_WritesBuiltReportToDiskOnce()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // The built report was written to disk exactly once, and it is the same report whose id the
        // runner returns.
        var written = Assert.Single(h.ReportWriter.Written);
        Assert.Equal(result.ReportId, written.Id);
    }

    [Fact]
    public async Task GenerateReportTrue_ThreadsCollectionSummaryIntoReportFooter()
    {
        var companyId = Guid.NewGuid();

        // A collector whose run summary reports a failed source; the runner must thread it into the
        // report so the renderer emits the Collection summary footer with that failure.
        var summary = new CollectionSummary(
            SourcesChecked: 2,
            SourcesSucceeded: 1,
            SourcesFailed: 1,
            ItemsCollected: 1,
            Failures: [new SourceFailure("Broken Feed", "https://broken.test/rss", "HTTP 500")]);
        var collector = new FakeEvidenceCollector(new CollectionResult([BuildCollected()], summary));
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var written = Assert.Single(h.ReportWriter.Written);
        Assert.Contains("## Collection summary", written.MarkdownContent, StringComparison.Ordinal);
        Assert.Contains(
            "Radar checked 2 source(s) this run; 1 could not be read.",
            written.MarkdownContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "- Broken Feed (https://broken.test/rss): HTTP 500",
            written.MarkdownContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateReportFalse_DoesNotWriteReportToDisk()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        Assert.Empty(h.ReportWriter.Written);
    }

    [Fact]
    public async Task InjectedClock_IsHonoured_NoUtcNowLeak()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        var snapshots = await h.Scores.GetSnapshotsForCompanyAsync(companyId, default);
        var snapshot = Assert.Single(snapshots);
        Assert.Equal(FixedNow, snapshot.CreatedAtUtc);

        var report = await h.Reports.GetByIdAsync(result.ReportId!.Value, default);
        Assert.NotNull(report);
        Assert.Equal(FixedNow, report!.CreatedAtUtc);
    }

    [Fact]
    public async Task Determinism_TwoRunsOverFreshState_ReturnEqualCounts()
    {
        var companyId = Guid.NewGuid();

        async Task<RadarPipelineResult> RunOnceAsync()
        {
            // Each run uses a fresh harness (fresh in-memory state), so the same collected evidence is
            // brand-new to it. The excerpt stays present in the raw text so the signal validates.
            var collector = new FakeEvidenceCollector([BuildCollected()]);
            var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

            var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
            await SeedCompanyAsync(h, companyId);
            return await h.Runner.RunAsync(default);
        }

        var first = await RunOnceAsync();
        var second = await RunOnceAsync();

        Assert.Equal(first.EvidenceCollected, second.EvidenceCollected);
        Assert.Equal(first.EvidenceNew, second.EvidenceNew);
        Assert.Equal(first.SignalsExtracted, second.SignalsExtracted);
        Assert.Equal(first.SignalsValid, second.SignalsValid);
        Assert.Equal(first.SignalsApproved, second.SignalsApproved);
        Assert.Equal(first.SignalsNeedingReview, second.SignalsNeedingReview);
        Assert.Equal(first.CompaniesScored, second.CompaniesScored);
    }

    [Fact]
    public async Task FreshlyCollectedEvidence_WithNoPublishedAt_IsScoredInSameRun()
    {
        // Part B regression: the runner must capture asOfUtc AFTER collection. The advancing clock
        // makes the post-collection asOfUtc strictly greater than the evidence's CollectedAtUtc. With
        // no PublishedAtUtc, ObservedAtUtc falls back to CollectedAtUtc, which is at the (start, end]
        // window's inclusive end — so the signal scores. If asOfUtc were captured BEFORE collection
        // (the pre-fix bug), ObservedAtUtc would sort just AFTER the window end and the signal would be
        // dropped from scoring (CompaniesScored snapshot would have no contributing signals).
        var companyId = Guid.NewGuid();

        // Position the advancing clock's base so the freshly-stamped ObservedAtUtc sits inside both the
        // 30-day scoring window and the 7-day report period (both end at the post-collection asOfUtc).
        var clock = new AdvancingTimeProvider(FixedNow, TimeSpan.FromSeconds(1));

        // Build collected evidence with NO PublishedAt so the mapped ObservedAtUtc falls back to the
        // clock-stamped CollectedAt. The collector stamps CollectedAt from the advancing clock.
        var template = new CollectedEvidence(
            SourceType: EvidenceSourceType.LocalFile,
            SourceName: "Northwind Newsroom",
            SourceUrl: "https://example.com/nw",
            Title: "Northwind Robotics customer win",
            RawText: RawText,
            PublishedAt: null,
            CollectedAt: FixedNow,
            Metadata: new Dictionary<string, string> { ["quality"] = "High" });

        var collector = new ClockStampingCollector(clock, template);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = true }, clock);
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.True(result.CompaniesScored >= 1);

        // The snapshot must reflect the freshly collected signal: at least one contributing
        // evidence link (provenance) ties the snapshot to the in-window signal. The mapper assigned the
        // evidence id, so discover it from the persisted signal.
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        var snapshots = await h.Scores.GetSnapshotsForCompanyAsync(companyId, default);
        var snapshot = Assert.Single(snapshots);
        var links = await h.Scores.GetLinksForSnapshotAsync(snapshot.Id, default);
        var link = Assert.Single(links);
        Assert.Equal(signal.EvidenceId, link.EvidenceId);
    }

    [Fact]
    public async Task DiWiring_ComposesAndRunsOverTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "radar-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // One evidence JSON document for the local-file collector.
            var json = """
            {
              "sourceType": "PressRelease",
              "sourceName": "Northwind Newsroom",
              "sourceUrl": "https://example.com/nw",
              "title": "Northwind Robotics customer win",
              "summary": "A summary.",
              "rawText": "Northwind Robotics announced a major new customer win with a Fortune 100 partner today.",
              "publishedAtUtc": "2026-02-06T00:00:00Z",
              "quality": "High"
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempDir, "evidence-1.json"), json);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddInMemoryRadarPersistence();
            services.AddRadarApplicationServices();
            // The collection-health validator (spec 98) depends on ICompanySeedSource; register the
            // local-file seed (degrades to an empty seed when the file is absent, so no warnings).
            services.AddLocalFileCompanySeed(Path.Combine(tempDir, "companies.json"));
            services.AddLocalFileCollector(tempDir);
            services.AddFileRawEvidenceStore(Path.Combine(tempDir, "raw"));
            services.AddFileSignalStore(Path.Combine(tempDir, "signals"));
            services.AddFileScoreStore(Path.Combine(tempDir, "scores"));
            services.AddFileReportWriter(Path.Combine(tempDir, "reports"));
            services.AddFilePipelineRunStore(Path.Combine(tempDir, "runs"));
            services.AddFileScoringConfigStore(Path.Combine(tempDir, "scoring-configs"));
            services.AddRadarPipeline();

            using var provider = services.BuildServiceProvider();

            // Seed a company through the registered repository so the mention can resolve.
            var companies = provider.GetRequiredService<
                Radar.Application.Abstractions.Persistence.ICompanyRepository>();
            await companies.AddAsync(
                new CompanyBuilder().WithName(CompanyName).WithTicker("NWR").Build(), default);

            var pipeline = provider.GetRequiredService<IRadarPipeline>();
            var result = await pipeline.RunAsync(default);

            Assert.Equal(1, result.EvidenceCollected);
            Assert.Equal(1, result.EvidenceNew);
            Assert.True(result.CompaniesScored >= 1);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MultiCollector_RunsAllAndMergesEvidence_OrderedByCollectorName()
    {
        var companyId = Guid.NewGuid();

        // Distinct evidence items so they map to distinct content hashes and both survive dedupe.
        var aEvidence = new CollectedEvidence(
            SourceType: EvidenceSourceType.Filing,
            SourceName: "SEC EDGAR",
            SourceUrl: "https://sec.example/a",
            Title: "Northwind Robotics customer win (filing)",
            RawText: RawText,
            PublishedAt: Observed,
            CollectedAt: FixedNow,
            Metadata: new Dictionary<string, string> { ["quality"] = "High" });

        // The lexically-later collector ("ZZZ") emits a duplicate that COLLIDES with AAA's canonical
        // item (identical title+rawText → identical content hash) PLUS one distinct item. Because the
        // runner orders collectors by CollectorName ordinal, AAA is processed first and wins the
        // insert-only ContentHash dedupe; ZZZ's colliding duplicate is dropped, its distinct item kept.
        var zCollide = aEvidence with { SourceName = "Gov Contracts", SourceUrl = "https://gov.example/dup" };
        var zDistinct = new CollectedEvidence(
            SourceType: EvidenceSourceType.GovernmentContract,
            SourceName: "Gov Contracts",
            SourceUrl: "https://gov.example/b",
            Title: "Northwind Robotics federal award",
            RawText: "Northwind Robotics won a multi-year federal contract award this quarter.",
            PublishedAt: Observed,
            CollectedAt: FixedNow,
            Metadata: new Dictionary<string, string> { ["quality"] = "High" });

        var aCollector = new ConfigurableCollector(
            "AAA",
            EvidenceSourceType.Filing,
            new CollectionResult(
                [aEvidence],
                new CollectionSummary(1, 1, 0, 1, [])));
        var zCollector = new ConfigurableCollector(
            "ZZZ",
            EvidenceSourceType.GovernmentContract,
            new CollectionResult(
                [zCollide, zDistinct],
                new CollectionSummary(
                    SourcesChecked: 1,
                    SourcesSucceeded: 0,
                    SourcesFailed: 1,
                    ItemsCollected: 2,
                    Failures: [new SourceFailure("Gov Contracts", "https://gov.example", "HTTP 503")])));

        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        // Pass collectors DI-registration-shuffled (Z before A) to prove the runner sorts by name.
        var h = new Harness(
            [zCollector, aCollector], extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // Both collectors ran.
        Assert.True(aCollector.WasCalled);
        Assert.True(zCollector.WasCalled);

        // Three items were collected (AAA: 1, ZZZ: 2); the colliding duplicate dedupes away, so two
        // distinct evidence items are stored.
        Assert.Equal(3, result.EvidenceCollected);
        Assert.Equal(2, result.EvidenceNew);

        // The aggregated summary sums the per-collector counts.
        Assert.Equal(2, result.Collection.SourcesChecked);
        Assert.Equal(1, result.Collection.SourcesFailed);
        Assert.Equal(3, result.Collection.ItemsCollected);

        // The canonical (colliding) hash is stored exactly once, traced to AAA's SourceType (Filing)
        // because AAA is processed first under the CollectorName-ordinal order.
        var canonical = new CollectedEvidenceMapper(
            new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance)
            .ToEvidenceItem(aEvidence);
        var stored = await h.Evidence.GetByContentHashAsync(canonical.ContentHash, default);
        Assert.NotNull(stored);
        Assert.Equal(EvidenceSourceType.Filing, stored!.SourceType);

        // CROSS-COLLECTOR POLICY (spec 145), asserted rather than implied: AAA's Filing and ZZZ's
        // GovernmentContract carry the SAME normalized title+body and therefore the same content hash,
        // differing only in source name / URL / source type — all of which are deliberately excluded from
        // identity. So they are ONE evidence record, and the id is now reproducible OUTSIDE the run: the
        // freshly-mapped canonical item has the very id the pipeline stored. Pre-145 the mapper minted a
        // fresh Guid per map, so this equality could not hold and the stored item could only be found by
        // content hash.
        Assert.Equal(canonical.Id, stored.Id);
        Assert.Equal(
            EvidenceIdentity.ForContentHash(canonical.ContentHash), stored.Id);
    }

    [Fact]
    public async Task Run_RecordsTheCollectorOnEveryNewlyStoredEvidenceItem()
    {
        // Spec 146: a radar-formula-v9 collector channel selects on the RECORDED provenance of each signal's
        // evidence — and before this slice there was none (SourceType is shared by several collectors,
        // SourceName is the feed, and CollectionResultMerger discards per-collector attribution entirely).
        // The runner therefore stamps it in the collector loop, before the merge, which is the last moment
        // the information exists.
        var companyId = Guid.NewGuid();

        var alpha = BuildCollected();
        var beta = BuildCollected("Northwind Robotics signed a second, unrelated multi-year agreement.");

        var alphaCollector = new ConfigurableCollector(
            "AAA", EvidenceSourceType.LocalFile, AsResult([alpha]));
        var betaCollector = new ConfigurableCollector(
            "ZZZ", EvidenceSourceType.GovernmentContract, AsResult([beta]));

        var h = new Harness(
            [betaCollector, alphaCollector],
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);
        Assert.Equal(2, result.EvidenceNew);

        var mapper = new CollectedEvidenceMapper(
            new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance);

        var storedAlpha = await h.Evidence.GetByContentHashAsync(
            mapper.ToEvidenceItem(alpha).ContentHash, default);
        var storedBeta = await h.Evidence.GetByContentHashAsync(
            mapper.ToEvidenceItem(beta).ContentHash, default);

        Assert.Equal("AAA", CollectionProvenanceMetadata.Read(storedAlpha));
        Assert.Equal("ZZZ", CollectionProvenanceMetadata.Read(storedBeta));

        // The stamp is metadata only: it is NOT an input to evidence identity (spec 145 — the normalized
        // title+body hash alone) nor to the content hash, so the stored id is still the one an unstamped
        // mapping of the same content produces. No evidence id moves; no dedupe decision changes.
        Assert.Equal(mapper.ToEvidenceItem(alpha).Id, storedAlpha!.Id);
        Assert.Equal(mapper.ToEvidenceItem(alpha).ContentHash, storedAlpha.ContentHash);
    }

    [Fact]
    public async Task Run_WritesExactlyOneRunRecord_MatchingResultCounts()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // Exactly one run record is written per run, and every count on it equals the returned result.
        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(result.EvidenceCollected, record.EvidenceCollected);
        Assert.Equal(result.EvidenceNew, record.EvidenceNew);
        Assert.Equal(result.SignalsExtracted, record.SignalsExtracted);
        Assert.Equal(result.SignalsValid, record.SignalsValid);
        Assert.Equal(result.SignalsApproved, record.SignalsApproved);
        Assert.Equal(result.SignalsNeedingReview, record.SignalsNeedingReview);
        Assert.Equal(result.CompaniesScored, record.CompaniesScored);
        Assert.Equal(result.SourcesChecked, record.SourcesChecked);
        Assert.Equal(result.SourcesFailed, record.SourcesFailed);

        // The record's ReportId matches the result and is non-null when a report was generated.
        Assert.Equal(result.ReportId, record.ReportId);
        Assert.NotNull(record.ReportId);

        // The record is stamped with the run's single instant (AD-7).
        Assert.Equal(FixedNow, record.CreatedAtUtc);
    }

    [Fact]
    public async Task Run_RunRecord_HasOrderedCollectorNames()
    {
        var companyId = Guid.NewGuid();

        var aCollector = new ConfigurableCollector(
            "AAA",
            EvidenceSourceType.Filing,
            new CollectionResult([BuildCollected()], CollectionSummary.Empty));
        var zCollector = new ConfigurableCollector(
            "ZZZ",
            EvidenceSourceType.GovernmentContract,
            new CollectionResult([], CollectionSummary.Empty));

        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        // Pass collectors DI-registration-shuffled (Z before A) to prove the record carries the
        // runner's stable CollectorName-ordinal order, not the registration order.
        var h = new Harness(
            [zCollector, aCollector], extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(new[] { "AAA", "ZZZ" }, record.Collectors);
    }

    [Fact]
    public async Task Run_SurfacesCollectionHealthWarnings_IntoRunRecord_WithoutChangingCounters()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var warning = new CollectionHealthWarning(
            Code: "feeds-lost-before-collection",
            Severity: CollectionHealthSeverity.Warning,
            FeedType: "sec",
            DeclaredInSeed: 7,
            ReachedCollectors: 0,
            Message: "Seed declares 7 'sec' feed(s) but only 0 reached the collectors.");
        var validator = new StubCollectionHealthValidator(new CollectionHealthReport([warning]));

        var h = new Harness(
            collector,
            extractor,
            new PipelineOptions { GenerateReport = true },
            healthValidator: validator);
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // The warning is surfaced verbatim on the durable run record.
        var record = Assert.Single(h.RunStore.Written);
        Assert.NotNull(record.CollectionWarnings);
        var surfaced = Assert.Single(record.CollectionWarnings!);
        Assert.Equal(warning, surfaced);

        // The health check is side-effect-free: scoring counters/output are unchanged by the warning.
        Assert.Equal(1, result.EvidenceCollected);
        Assert.Equal(1, result.EvidenceNew);
        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(1, result.SignalsApproved);
        Assert.Equal(1, result.CompaniesScored);
    }

    [Fact]
    public async Task Run_CleanCollectionHealth_RunRecordCarriesEmptyWarnings()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        // Default stub returns CollectionHealthReport.Empty.
        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.NotNull(record.CollectionWarnings);
        Assert.Empty(record.CollectionWarnings!);
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 169: per-COLLECTOR run provenance. Captured inside the collector loop, BEFORE the merge discards
    // collector identity — the aggregate CollectionSummary cannot separate one collector's failure from
    // another's, which is the correction AD-16's 2026-08-03 amendment records.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_RecordsEachCollectorsOwnUnmergedSummary_InTheStableCollectorOrder()
    {
        var companyId = Guid.NewGuid();
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var aCollector = new ConfigurableCollector(
            "AAA",
            EvidenceSourceType.LocalFile,
            new CollectionResult(
                [BuildCollected()],
                new CollectionSummary(3, 3, 0, 1, [])));

        var zCollector = new ConfigurableCollector(
            "ZZZ",
            EvidenceSourceType.RssFeed,
            new CollectionResult(
                [],
                new CollectionSummary(2, 1, 1, 0, [new SourceFailure("z-feed", "https://z", "HTTP 500")])));

        // Registration-shuffled (Z before A) to prove the record carries the stable CollectorName order.
        var h = new Harness(
            [zCollector, aCollector], extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.NotNull(record.CollectorRuns);
        Assert.Equal(["AAA", "ZZZ"], record.CollectorRuns!.Select(c => c.CollectorName));

        var a = record.CollectorRuns![0];
        Assert.Equal(3, a.SourcesChecked);
        Assert.Equal(0, a.SourcesFailed);
        Assert.Empty(a.Failures);
        Assert.Null(a.CompanyCoverage);

        // The per-collector rows keep what the aggregate loses: WHICH collector failed, and on what.
        var z = record.CollectorRuns![1];
        Assert.Equal(1, z.SourcesFailed);
        Assert.Equal("HTTP 500", Assert.Single(z.Failures).Reason);
        Assert.Equal(5, record.SourcesChecked); // the aggregate is unchanged …
        Assert.Equal(1, record.SourcesFailed);  // … and still cannot say which collector it was.
    }

    [Fact]
    public async Task Run_CarriesACollectorsPerCompanyCoverage_ThroughToTheRunRecord()
    {
        var companyId = Guid.NewGuid();
        var coveredCompany = Guid.NewGuid();
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var collector = new ConfigurableCollector(
            "newssearch",
            EvidenceSourceType.NewsArticle,
            new CollectionResult(
                [BuildCollected()],
                new CollectionSummary(1, 1, 0, 1, []),
                [new CollectorCompanyCoverage(coveredCompany, 1, 1, false, [])]));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var coverage = Assert.Single(
            Assert.Single(Assert.Single(h.RunStore.Written).CollectorRuns!).CompanyCoverage!);
        Assert.Equal(coveredCompany, coverage.CompanyId);
        Assert.Empty(coverage.Issues);
    }

    [Fact]
    public async Task Run_AFeedInventoryHealthWarning_StampsCollectionHealthMismatchOnThatCollectorsRows()
    {
        // The collector is handed the collection CONTEXT, never the reconciliation report, so this token can
        // only be added by the pass. Over-marking is the safe direction: it costs coverage, never invents it.
        var companyId = Guid.NewGuid();
        var covered = Guid.NewGuid();
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var newsSearch = new ConfigurableCollector(
            "newssearch",
            EvidenceSourceType.NewsArticle,
            new CollectionResult(
                [BuildCollected()],
                new CollectionSummary(1, 1, 0, 1, []),
                [
                    new CollectorCompanyCoverage(
                        covered, 1, 1, false, [CollectionCoverageIssues.ResultLimitReached]),
                ]));

        var validator = new StubCollectionHealthValidator(new CollectionHealthReport(
        [
            new CollectionHealthWarning(
                "feeds-lost-before-collection",
                CollectionHealthSeverity.Warning,
                FeedType: "newssearch",
                DeclaredInSeed: 43,
                ReachedCollectors: 40,
                Message: "Seed declares 43 'newssearch' feed(s) but only 40 reached the collectors."),
        ]));

        var h = new Harness(
            newsSearch, extractor, new PipelineOptions { GenerateReport = false }, healthValidator: validator);
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var coverage = Assert.Single(
            Assert.Single(Assert.Single(h.RunStore.Written).CollectorRuns!).CompanyCoverage!);

        // Ordinally sorted, and the collector's own token is preserved rather than replaced.
        Assert.Equal(
            [CollectionCoverageIssues.CollectionHealthMismatch, CollectionCoverageIssues.ResultLimitReached],
            coverage.Issues);
    }

    [Fact]
    public async Task Run_AHealthWarningForAnotherFeedType_LeavesThisCollectorsCoverageAlone()
    {
        var companyId = Guid.NewGuid();
        var covered = Guid.NewGuid();
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var newsSearch = new ConfigurableCollector(
            "newssearch",
            EvidenceSourceType.NewsArticle,
            new CollectionResult(
                [BuildCollected()],
                new CollectionSummary(1, 1, 0, 1, []),
                [new CollectorCompanyCoverage(covered, 1, 1, false, [])]));

        var validator = new StubCollectionHealthValidator(new CollectionHealthReport(
        [
            new CollectionHealthWarning(
                "feeds-lost-before-collection",
                CollectionHealthSeverity.Warning,
                FeedType: "sec",
                DeclaredInSeed: 43,
                ReachedCollectors: 40,
                Message: "Seed declares 43 'sec' feed(s) but only 40 reached the collectors."),
        ]));

        var h = new Harness(
            newsSearch, extractor, new PipelineOptions { GenerateReport = false }, healthValidator: validator);
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var coverage = Assert.Single(
            Assert.Single(Assert.Single(h.RunStore.Written).CollectorRuns!).CompanyCoverage!);
        Assert.Empty(coverage.Issues);
    }

    [Fact]
    public async Task Run_RecordsNoCompanyFilter_SoAFullRunCanSupplyACoverageCheckpoint()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Null(record.CompanyFilter);
        Assert.NotNull(record.CollectorRuns);
    }

    [Fact]
    public async Task Run_WithGenerateReportFalse_RunRecordReportIdIsNull()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Null(record.ReportId);
        Assert.Equal(result.ReportId, record.ReportId);
    }

    [Fact]
    public async Task Run_WritesEffectiveScoringConfig_OncePerRun_NotOncePerCompany()
    {
        // A multi-company universe: the effective scoring config is identical for every company, so the
        // runner must persist it ONCE per run (content-addressed, insert-if-new), not once per scored
        // company. The config-store write is best-effort and must not change any run-summary count.
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, Guid.NewGuid(), "Northwind Robotics");
        await SeedCompanyAsync(h, Guid.NewGuid(), "Contoso Instruments");
        await SeedCompanyAsync(h, Guid.NewGuid(), "Fabrikam Dynamics");

        var result = await h.Runner.RunAsync(default);

        // Every company was scored, but the config store was written exactly once.
        Assert.Equal(3, result.CompaniesScored);
        Assert.Equal(1, h.ScoringConfigStore.WriteCallCount);

        // The written config is the engine's effective config: its fingerprint matches every snapshot's
        // ScoringConfigVersion stamp (provenance completion — the stamp dereferences to these weights).
        var written = Assert.Single(h.ScoringConfigStore.Written);
        foreach (var (snapshot, _) in h.ScoreStore.Written)
        {
            Assert.Equal(written.Fingerprint, snapshot.ScoringConfigVersion);
        }
    }

    /// <summary>
    /// A fake directional filing signal source that records the candidate evidence it receives and emits
    /// one directional signal per candidate (via a caller-supplied factory) so the runner-threading test
    /// stays decoupled from the real reader/analyzer (those live behind Infrastructure interfaces).
    /// </summary>
    private sealed class FakeDirectionalFilingSignalSource(
        Func<EvidenceItem, ExtractedSignal> signalFor) : IDirectionalFilingSignalSource
    {
        public List<EvidenceItem> ReceivedCandidates { get; } = new();

        /// <summary>How many times the (AI) directional read ran — must stay 1 per run, spec 137.</summary>
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
            IReadOnlyList<EvidenceItem> candidateEvidence, DateTimeOffset asOfUtc, CancellationToken ct)
        {
            CallCount++;
            ReceivedCandidates.AddRange(candidateEvidence);
            IReadOnlyList<DirectionalFilingSignal> produced = candidateEvidence
                .Select(ev => new DirectionalFilingSignal(signalFor(ev), ev))
                .ToList();
            return Task.FromResult(produced);
        }

        public string ScoringDescriptor() => "directional-filing:str=6;nov=6;minconf=0.6";
    }

    /// <summary>An earnings-8-K Filing collected-evidence, in both windows so its signal can score.</summary>
    private static CollectedEvidence BuildFilingCollected() =>
        new(
            SourceType: EvidenceSourceType.Filing,
            SourceName: "Northwind — SEC",
            SourceUrl:
                "https://www.sec.gov/Archives/edgar/data/1/000104952126000011/0001049521-26-000011-index.htm",
            Title:
                "8-K — Results (2026-02-06) [items: 2.02,9.01] Items: Results of Operations and Financial Condition.",
            RawText: "8-K filing accession 0001049521-26-000011 filed 2026-02-06: Report. 8-K item codes: 2.02,9.01.",
            PublishedAt: Observed,
            CollectedAt: FixedNow,
            Metadata: new Dictionary<string, string> { ["quality"] = "High" });

    [Fact]
    public async Task DirectionalFilingSource_ThreadsPositiveGuidanceChange_ThroughStandardPath()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildFilingCollected()]);

        // The deterministic extractor emits nothing so the ONLY stored signal is the directional one — the
        // assertions then isolate the enrichment path.
        var extractor = new AnyEvidenceSignalExtractor(new([], "summary"));

        // The directional signal resolves to the seeded company by name, carries a verbatim excerpt from
        // the evidence (its Title, preserved by the mapper), and the AI rationale in Reason.
        var source = new FakeDirectionalFilingSignalSource(ev => new ExtractedSignal(
            CompanyMention: CompanyName,
            SignalType: "GuidanceChange",
            Direction: "Positive",
            Strength: 6,
            Novelty: 6,
            Confidence: 0.9m,
            SupportingExcerpt: ev.Title,
            Reason: "Directional read: revenue up, guidance raised."));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: source);
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // The source received the run's newly-stored Filing evidence as a candidate.
        var candidate = Assert.Single(source.ReceivedCandidates);
        Assert.Equal(EvidenceSourceType.Filing, candidate.SourceType);

        // Exactly one signal stored: a Positive GuidanceChange, resolved + approved like a keyword signal.
        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(1, result.SignalsApproved);

        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(SignalType.GuidanceChange, signal.Type);
        Assert.Equal(SignalDirection.Positive, signal.Direction);
        Assert.Equal(companyId, signal.CompanyId);
        Assert.Equal(SignalReviewStatus.Approved, signal.ReviewStatus);
        Assert.Contains("revenue up, guidance raised", signal.Reason, StringComparison.Ordinal);

        // Provenance: the signal references the stored filing evidence, and a review traces to the signal.
        Assert.Equal(candidate.Id, signal.EvidenceId);
        Assert.NotNull(await h.Evidence.GetByIdAsync(signal.EvidenceId, default));
        var review = Assert.Single(await h.Reviews.GetBySignalAsync(signal.Id, default));
        Assert.Equal(signal.Id, review.SignalId);
    }

    [Fact]
    public async Task DirectionalFilingSource_ThreadsCollectorHint_ResolvesSignalToHintedCompany()
    {
        var companyId = Guid.NewGuid();

        // The Filing evidence carries the seeded company's ticker as a collector hint. The directional
        // signal's mention is generic and would NOT resolve on its own — only the threaded hint can
        // resolve it, so an approved signal proves the runner passes directional.Evidence's hints (not [])
        // into the resolver.
        var collector = new FakeEvidenceCollector(
            [BuildFilingCollected() with { CompanyHints = ["NWR"] }]);
        var extractor = new AnyEvidenceSignalExtractor(new([], "summary"));

        var source = new FakeDirectionalFilingSignalSource(ev => new ExtractedSignal(
            CompanyMention: "Some Generic Vendor Name",
            SignalType: "GuidanceChange",
            Direction: "Positive",
            Strength: 6,
            Novelty: 6,
            Confidence: 0.9m,
            SupportingExcerpt: ev.Title,
            Reason: "Directional read: revenue up, guidance raised."));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: source);
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(1, result.SignalsApproved);

        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(companyId, signal.CompanyId);
        Assert.Equal(SignalReviewStatus.Approved, signal.ReviewStatus);
    }

    [Fact]
    public async Task NullDirectionalFilingSource_IsNoOp_NoDirectionalSignal()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildFilingCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([], "summary"));

        // No directional source (AI disabled): the enrichment step is skipped entirely, so a Filing
        // evidence yields no directional signal — the default byte-for-byte behaviour.
        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.EvidenceNew);
        Assert.Equal(0, result.SignalsExtracted);
        Assert.Equal(0, result.SignalsValid);

        var observed = await h.Signals.GetObservedBetweenAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default);
        Assert.Empty(observed);
    }

    // A verbatim substring of BuildFilingCollected()'s Title, so a Neutral GuidanceChange carrying it as
    // its SupportingExcerpt passes the mapper's provenance check (excerpt must be traceable to the evidence).
    private const string FilingExcerpt = "Results of Operations and Financial Condition";

    /// <summary>The deterministic (spec 57) Neutral GuidanceChange an earnings-2.02 filing yields.</summary>
    private static ExtractedSignal NeutralGuidanceChange(string excerpt = FilingExcerpt) =>
        new(
            CompanyMention: CompanyName,
            SignalType: "GuidanceChange",
            Direction: "Neutral",
            Strength: 3,
            Novelty: 3,
            Confidence: 0.6m,
            SupportingExcerpt: excerpt,
            Reason: "Results of operations reported by the company.");

    /// <summary>The directional (spec 75) Positive GuidanceChange the AI earnings read yields.</summary>
    private static ExtractedSignal PositiveGuidanceChange(string excerpt) =>
        new(
            CompanyMention: CompanyName,
            SignalType: "GuidanceChange",
            Direction: "Positive",
            Strength: 6,
            Novelty: 6,
            Confidence: 0.9m,
            SupportingExcerpt: excerpt,
            Reason: "Directional read: revenue up, guidance raised.");

    /// <summary>
    /// An in-test extractor that returns caller-chosen signals per evidence (unlike
    /// <see cref="AnyEvidenceSignalExtractor"/> which returns a fixed output for ANY evidence). Lets the
    /// scoped-suppression test emit different signals for two distinct filings.
    /// </summary>
    private sealed class PerEvidenceSignalExtractor(
        Func<EvidenceItem, IReadOnlyList<ExtractedSignal>> signalsFor) : ISignalExtractor
    {
        public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(new ExtractSignalsOutput(signalsFor(evidence), "summary"));
    }

    /// <summary>
    /// A fake directional filing source that emits a directional signal only for the evidence a
    /// caller-supplied factory returns non-null for (returning null models below-MinConfidence /
    /// Mixed / Unknown / failure — i.e. "no directional read for this filing"). Lets tests model empty,
    /// full, and scoped directional coverage without the real reader/analyzer (behind Infrastructure).
    /// </summary>
    private sealed class SelectiveDirectionalFilingSignalSource(
        Func<EvidenceItem, ExtractedSignal?> signalFor) : IDirectionalFilingSignalSource
    {
        public Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
            IReadOnlyList<EvidenceItem> candidateEvidence, DateTimeOffset asOfUtc, CancellationToken ct)
        {
            IReadOnlyList<DirectionalFilingSignal> produced = candidateEvidence
                .Select(ev => (Evidence: ev, Signal: signalFor(ev)))
                .Where(x => x.Signal is not null)
                .Select(x => new DirectionalFilingSignal(x.Signal!, x.Evidence))
                .ToList();
            return Task.FromResult(produced);
        }

        public string ScoringDescriptor() => "directional-filing:str=6;nov=6;minconf=0.6";
    }

    /// <summary>A second, distinct earnings-8-K Filing (different content hash) the directional source
    /// can choose NOT to cover, for the scoped-suppression test.</summary>
    private static CollectedEvidence BuildSecondFilingCollected() =>
        new(
            SourceType: EvidenceSourceType.Filing,
            SourceName: "Northwind — SEC",
            SourceUrl:
                "https://www.sec.gov/Archives/edgar/data/1/000104952126000022/0001049521-26-000022-index.htm",
            Title:
                "8-K — Results (2026-02-05) [items: 2.02,9.01] Second filing Results of Operations and Financial Condition.",
            RawText: "8-K filing accession 0001049521-26-000022 filed 2026-02-05: Report. 8-K item codes: 2.02,9.01.",
            PublishedAt: Observed,
            CollectedAt: FixedNow,
            Metadata: new Dictionary<string, string> { ["quality"] = "High" });

    [Fact]
    public async Task DirectionalRead_SupersedesDeterministicNeutralGuidanceChange_ForSameFiling()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildFilingCollected()]);

        // The deterministic extractor yields the Neutral GuidanceChange (spec 57) for the 2.02 filing.
        var extractor = new AnyEvidenceSignalExtractor(new([NeutralGuidanceChange()], "summary"));

        // The directional source returns one Positive GuidanceChange over the SAME filing evidence.
        var source = new SelectiveDirectionalFilingSignalSource(ev => PositiveGuidanceChange(ev.Title));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: source);
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // The filing's GuidanceChange is counted ONCE (the directional), not twice — the Neutral is
        // suppressed before store and increments no counter.
        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(1, result.SignalsValid);
        Assert.Equal(1, result.SignalsApproved);

        // Exactly one GuidanceChange persisted for that evidence, and it is the directional (Positive) one.
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(SignalType.GuidanceChange, signal.Type);
        Assert.Equal(SignalDirection.Positive, signal.Direction);

        // No Neutral GuidanceChange was stored for the filing (superseded).
        Assert.DoesNotContain(
            signals, s => s.Type == SignalType.GuidanceChange && s.Direction == SignalDirection.Neutral);

        // Provenance: the surviving directional signal references the same filing evidence.
        Assert.NotNull(await h.Evidence.GetByIdAsync(signal.EvidenceId, default));

        // On-disk twin: exactly one signal mirrored, the directional one — the Neutral has no on-disk file.
        var write = Assert.Single(h.SignalStore.Written);
        Assert.Equal(signal.Id, write.Signal.Id);
        Assert.Equal(SignalDirection.Positive, write.Signal.Direction);
    }

    [Fact]
    public async Task NoDirectionalRead_LeavesDeterministicNeutralGuidanceChangeStanding()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildFilingCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([NeutralGuidanceChange()], "summary"));

        // The source returns NOTHING for the filing (below MinConfidence / Mixed / Unknown / failure), so
        // no supersede occurs — the deterministic Neutral must stand exactly as today.
        var source = new SelectiveDirectionalFilingSignalSource(_ => null);

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: source);
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(1, result.SignalsValid);

        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(SignalType.GuidanceChange, signal.Type);
        Assert.Equal(SignalDirection.Neutral, signal.Direction);
    }

    [Fact]
    public async Task NullDirectionalSource_LeavesDeterministicNeutralGuidanceChangeStanding()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildFilingCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([NeutralGuidanceChange()], "summary"));

        // AI disabled (null source): the supersede set is empty, nothing is suppressed — byte-for-byte
        // unchanged from the pre-spec-78 default.
        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.SignalsExtracted);
        Assert.Equal(1, result.SignalsValid);

        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(SignalType.GuidanceChange, signal.Type);
        Assert.Equal(SignalDirection.Neutral, signal.Direction);
    }

    [Fact]
    public async Task Supersede_IsScopedToTheCoveredFilingsGuidanceChangeOnly()
    {
        var companyId = Guid.NewGuid();

        // Two distinct in-window earnings filings. The directional source will cover only the first
        // (title contains "Second filing" distinguishes the uncovered one).
        var collector = new FakeEvidenceCollector([BuildFilingCollected(), BuildSecondFilingCollected()]);

        // Both filings get a deterministic Neutral GuidanceChange; the covered filing ALSO gets a
        // non-GuidanceChange (CustomerWin) signal that must survive the supersede.
        var extractor = new PerEvidenceSignalExtractor(ev =>
        {
            var list = new List<ExtractedSignal> { NeutralGuidanceChange(ev.Title) };
            if (!ev.Title.Contains("Second filing", StringComparison.Ordinal))
            {
                list.Add(new ExtractedSignal(
                    CompanyMention: CompanyName,
                    SignalType: "CustomerWin",
                    Direction: "Positive",
                    Strength: 4,
                    Novelty: 4,
                    Confidence: 0.8m,
                    SupportingExcerpt: ev.Title,
                    Reason: "Material customer win noted alongside results."));
            }

            return list;
        });

        // Directional coverage ONLY for the first filing (not the "Second filing").
        var source = new SelectiveDirectionalFilingSignalSource(ev =>
            ev.Title.Contains("Second filing", StringComparison.Ordinal)
                ? null
                : PositiveGuidanceChange(ev.Title));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: source);
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        var signals = await h.Signals.GetByCompanyAsync(companyId, default);

        // The covered filing keeps exactly one GuidanceChange — the directional Positive — and its Neutral
        // is gone; its non-GuidanceChange CustomerWin survives.
        var positive = Assert.Single(
            signals, s => s.Type == SignalType.GuidanceChange && s.Direction == SignalDirection.Positive);
        var coveredEvidenceId = positive.EvidenceId;
        Assert.DoesNotContain(
            signals,
            s => s.Type == SignalType.GuidanceChange
                 && s.Direction == SignalDirection.Neutral
                 && s.EvidenceId == coveredEvidenceId);
        Assert.Contains(
            signals, s => s.Type == SignalType.CustomerWin && s.EvidenceId == coveredEvidenceId);

        // The uncovered filing keeps its deterministic Neutral GuidanceChange (different EvidenceId).
        Assert.Contains(
            signals,
            s => s.Type == SignalType.GuidanceChange
                 && s.Direction == SignalDirection.Neutral
                 && s.EvidenceId != coveredEvidenceId);
    }

    [Fact]
    public async Task Cancellation_BeforeRun_ThrowsAndStoresNothing()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildFilingCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([NeutralGuidanceChange()], "summary"));
        var source = new SelectiveDirectionalFilingSignalSource(ev => PositiveGuidanceChange(ev.Title));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: source);
        await SeedCompanyAsync(h, companyId);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runner.RunAsync(cts.Token));

        // Nothing was stored before the run threw.
        var observed = await h.Signals.GetObservedBetweenAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default);
        Assert.Empty(observed);
        Assert.Empty(h.SignalStore.Written);
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 137 — N strategies over ONE collection pass.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Two strategies: "baseline" (primary, code-default weights) and "low-media" (non-primary, a different
    /// MediaReachWeight so its effective config — and therefore its fingerprint — genuinely differs).
    /// </summary>
    private static ScoringStrategySet TwoStrategies() =>
        new(
        [
            new ScoringStrategyDefinition(
                "baseline", "default", new ScoringWeights(), IsPrimary: true),
            new ScoringStrategyDefinition(
                "low-media", "low-media", new ScoringWeights { MediaReachWeight = 0.02 }, IsPrimary: false),
        ]);

    [Fact]
    public async Task TwoStrategies_ProduceTwoIndependentSnapshotSets_NeitherOverwritingTheOther()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        // The PRIMARY strategy's snapshot went to the legacy store; the non-primary one did NOT.
        var primaryWritten = Assert.Single(h.ScoreStores.Primary.Written);
        var secondaryWritten = Assert.Single(h.ScoreStores.For("low-media").Written);

        // Distinct snapshots — neither overwrites the other.
        Assert.NotEqual(primaryWritten.Snapshot.Id, secondaryWritten.Snapshot.Id);
        Assert.Equal(companyId, primaryWritten.Snapshot.CompanyId);
        Assert.Equal(companyId, secondaryWritten.Snapshot.CompanyId);

        // Human-readable strategy identity is stamped on both, including the primary.
        Assert.Equal("baseline", primaryWritten.Snapshot.StrategyName);
        Assert.Equal("low-media", secondaryWritten.Snapshot.StrategyName);

        // Different effective configs ⇒ different opaque generation stamps (the two series are correctly
        // NOT comparable with each other).
        Assert.NotNull(primaryWritten.Snapshot.ScoringConfigVersion);
        Assert.NotNull(secondaryWritten.Snapshot.ScoringConfigVersion);
        Assert.NotEqual(
            primaryWritten.Snapshot.ScoringConfigVersion, secondaryWritten.Snapshot.ScoringConfigVersion);

        // Provenance survives per strategy: each snapshot keeps its own ScoreEvidenceLink chain.
        Assert.NotEmpty(primaryWritten.Links);
        Assert.NotEmpty(secondaryWritten.Links);
        Assert.All(primaryWritten.Links, l => Assert.Equal(primaryWritten.Snapshot.Id, l.ScoreSnapshotId));
        Assert.All(secondaryWritten.Links, l => Assert.Equal(secondaryWritten.Snapshot.Id, l.ScoreSnapshotId));
    }

    [Fact]
    public async Task TwoStrategies_KeepTheSharedScoreRepository_PrimaryOnly()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, companyId);

        await h.Runner.RunAsync(default);

        // The shared repository — the one the weekly report reads — holds ONLY the primary's snapshot. If a
        // non-primary strategy wrote here, the report would silently rank a mixture of strategies.
        var shared = await h.Scores.GetSnapshotsForCompanyAsync(companyId, default);
        var snapshot = Assert.Single(shared);
        Assert.Equal("baseline", snapshot.StrategyName);

        // The non-primary strategy's snapshot exists — in its OWN repository.
        var secondary = h.ScoreRepositories.ForStrategy(h.StrategySet.Strategies[1]);
        var isolated = Assert.Single(await secondary.GetSnapshotsForCompanyAsync(companyId, default));
        Assert.Equal("low-media", isolated.StrategyName);
    }

    [Fact]
    public async Task TwoStrategies_WeeklyReport_RendersThePrimaryStrategyOnly()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = true },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // Exactly one item per company (not one per strategy), citing the PRIMARY snapshot.
        var items = await h.Reports.GetItemsAsync(result.ReportId!.Value, default);
        var item = Assert.Single(items);
        Assert.Equal(h.ScoreStores.Primary.Written[0].Snapshot.Id, item.ScoreSnapshotId);
        Assert.NotEqual(h.ScoreStores.For("low-media").Written[0].Snapshot.Id, item.ScoreSnapshotId);
    }

    [Fact]
    public async Task TwoStrategies_CollectAndDirectionalReadRunExactlyOnce()
    {
        // The whole point of the slice: N scorings over ONE collection pass. Nothing above the scoring stage
        // may run per strategy.
        var companyId = Guid.NewGuid();
        var collectorA = new ConfigurableCollector(
            "collector-a", EvidenceSourceType.LocalFile, AsResult([BuildCollected()]));
        var collectorB = new ConfigurableCollector(
            "collector-b", EvidenceSourceType.Filing, AsResult([BuildFilingCollected()]));

        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));
        var directional = new FakeDirectionalFilingSignalSource(ev => new ExtractedSignal(
            CompanyMention: CompanyName,
            SignalType: "GuidanceChange",
            Direction: "Positive",
            Strength: 6,
            Novelty: 6,
            Confidence: 0.9m,
            SupportingExcerpt: ev.Title,
            Reason: "Directional read: revenue up, guidance raised."));

        var h = new Harness(
            [collectorA, collectorB], extractor, new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: directional,
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, collectorA.CallCount);
        Assert.Equal(1, collectorB.CallCount);
        Assert.Equal(1, directional.CallCount);

        // Evidence and signals are collected/extracted/persisted once, not once per strategy.
        Assert.Equal(2, result.EvidenceCollected);
        Assert.Equal(2, result.EvidenceNew);
        var storedSignals = await h.Signals.GetByCompanyAsync(companyId, default);
        Assert.Equal(h.SignalStore.Written.Count, storedSignals.Count);

        // ...but BOTH strategies scored.
        Assert.Single(h.ScoreStores.Primary.Written);
        Assert.Single(h.ScoreStores.For("low-media").Written);
    }

    [Fact]
    public async Task TwoStrategies_CompaniesScored_CountsThePrimaryStrategyOnly()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, Guid.NewGuid());
        await SeedCompanyAsync(h, Guid.NewGuid(), "Acme Dynamics");

        var result = await h.Runner.RunAsync(default);

        // 2 companies × 2 strategies = 4 snapshots written, but the counter keeps its established meaning
        // ("how many companies were scored this run") rather than silently multiplying by strategy count.
        Assert.Equal(2, result.CompaniesScored);
        Assert.Equal(2, h.ScoreStores.Primary.Written.Count);
        Assert.Equal(2, h.ScoreStores.For("low-media").Written.Count);
    }

    [Fact]
    public async Task TwoStrategies_WriteOneEffectiveScoringConfigPerStrategy()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, Guid.NewGuid());
        await SeedCompanyAsync(h, Guid.NewGuid(), "Acme Dynamics");

        await h.Runner.RunAsync(default);

        // One content-addressed config write per strategy (NOT per company), and each snapshot's stamp
        // dereferences back to its own strategy's config.
        Assert.Equal(2, h.ScoringConfigStore.WriteCallCount);
        Assert.Equal(2, h.ScoringConfigStore.Written.Select(c => c.Fingerprint).Distinct().Count());
        Assert.Contains(
            h.ScoringConfigStore.Written,
            c => c.Fingerprint == h.ScoreStores.Primary.Written[0].Snapshot.ScoringConfigVersion);
        Assert.Contains(
            h.ScoringConfigStore.Written,
            c => c.Fingerprint == h.ScoreStores.For("low-media").Written[0].Snapshot.ScoringConfigVersion);
    }

    [Fact]
    public async Task TwoStrategies_RunRecord_ListsTheStrategiesThatRan()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, Guid.NewGuid());

        await h.Runner.RunAsync(default);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal(["baseline", "low-media"], record.Strategies!.ToArray());
        Assert.Equal("baseline", record.PrimaryStrategy);
    }

    [Fact]
    public async Task SingleDefaultStrategy_StampsTheStrategyName_AndChangesNothingElse()
    {
        // The byte-identical default: one synthesised "default" primary strategy writing to the legacy
        // store, with the strategy name as the only additive field on the snapshot.
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Equal(1, result.CompaniesScored);
        var written = Assert.Single(h.ScoreStores.Primary.Written);
        Assert.Equal(ScoringStrategySet.DefaultStrategyName, written.Snapshot.StrategyName);
        Assert.Same(h.ScoreStore, h.ScoreStores.ForStrategy(h.StrategySet.Primary));

        // Shared repository + report unchanged.
        Assert.Single(await h.Scores.GetSnapshotsForCompanyAsync(companyId, default));
        Assert.Single(await h.Reports.GetItemsAsync(result.ReportId!.Value, default));
        Assert.Equal(1, h.ScoringConfigStore.WriteCallCount);

        var record = Assert.Single(h.RunStore.Written);
        Assert.Equal([ScoringStrategySet.DefaultStrategyName], record.Strategies!.ToArray());
    }

    // ---- Spec 141: the strategy-identity tripwire runs BEFORE stage 1 ----

    /// <summary>
    /// A collector that counts <see cref="CollectAsync"/> calls, so a test can prove the startup tripwire
    /// fires <b>before</b> any collection work rather than after a wasted network pass.
    /// </summary>
    private sealed class CountingCollector(CollectionResult result) : IEvidenceCollector
    {
        public int CollectCallCount { get; private set; }

        public string CollectorName => "CountingCollector";

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            CollectCallCount++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task FirstRun_RecordsEachStrategysIdentity()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, Guid.NewGuid());

        await h.Runner.RunAsync(default);

        // One record per configured strategy NAME, each holding that strategy's own fingerprint.
        Assert.Equal(["baseline", "low-media"], h.ScoringConfigStore.StrategyFingerprints.Keys.Order().ToArray());
        foreach (var runtime in h.ScoreStores.Primary.Written)
        {
            Assert.Equal(
                runtime.Snapshot.ScoringConfigVersion,
                h.ScoringConfigStore.StrategyFingerprints[runtime.Snapshot.StrategyName!]);
        }
    }

    [Fact]
    public async Task EditedInPlaceStrategy_FailsFastBeforeAnyCollection()
    {
        // Spec 141: a strategy is immutable by convention because its NAME is the score-series key. A name
        // whose fingerprint moved was edited in place, and the run must fail fast — before Stage 1, so a
        // misconfiguration costs no network calls and leaves no partial run behind.
        var collector = new CountingCollector(AsResult([BuildCollected()]));
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));

        var configStore = new RecordingScoringConfigStore();
        configStore.StrategyFingerprints[ScoringStrategySet.DefaultStrategyName] = "radar-scoring-fp-stale";

        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            scoringConfigStore: configStore);
        await SeedCompanyAsync(h, Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.RunAsync(default));

        Assert.Contains(ScoringStrategySet.DefaultStrategyName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("radar-scoring-fp-stale", ex.Message, StringComparison.Ordinal);

        // Nothing ran: no collection, no evidence, no snapshot, no run record.
        Assert.Equal(0, collector.CollectCallCount);
        Assert.Empty(h.RawStore.Written);
        Assert.Empty(h.ScoreStores.Primary.Written);
        Assert.Empty(h.RunStore.Written);
    }

    [Fact]
    public async Task CollectorToggleBetweenRuns_DoesNotTripTheTripwire()
    {
        // THE acceptance criterion, end to end through the runner with the REAL SignalSourceDescriptor: run
        // once with two collectors, then again with three, sharing the recorded-identity store. Pre-141 the
        // collector set was hashed into the fingerprint, so the second run would have thrown.
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));
        var configStore = new RecordingScoringConfigStore();

        static ISignalSourceDescriptor DescriptorOver(params string[] names) =>
            new SignalSourceDescriptor(EnabledCollectorVocabulary.FromCollectors(
                names.Select(n => (IEvidenceCollector)new NamedNoOpCollector(n))));

        var companyId = Guid.NewGuid();

        var first = new Harness(
            new FakeEvidenceCollector([BuildCollected()]), extractor,
            new PipelineOptions { GenerateReport = false },
            scoringConfigStore: configStore,
            sourceDescriptor: DescriptorOver("rss", "sec-edgar"));
        await SeedCompanyAsync(first, companyId);
        await first.Runner.RunAsync(default);

        var recorded = configStore.StrategyFingerprints[ScoringStrategySet.DefaultStrategyName];

        var second = new Harness(
            new FakeEvidenceCollector([BuildCollected()]), extractor,
            new PipelineOptions { GenerateReport = false },
            scoringConfigStore: configStore,
            sourceDescriptor: DescriptorOver("rss", "sec-edgar", "fda"));
        await SeedCompanyAsync(second, companyId);

        // No throw, and the recorded identity is unmoved: only the recorded CollectionProvenance differs.
        await second.Runner.RunAsync(default);

        Assert.Equal(recorded, configStore.StrategyFingerprints[ScoringStrategySet.DefaultStrategyName]);
        Assert.Equal(
            recorded, Assert.Single(second.ScoreStores.Primary.Written).Snapshot.ScoringConfigVersion);
        Assert.Equal(
            "collectors=rss,sec-edgar;",
            Assert.Single(first.ScoreStores.Primary.Written).Snapshot.CollectionProvenance);
        Assert.Equal(
            "collectors=fda,rss,sec-edgar;",
            Assert.Single(second.ScoreStores.Primary.Written).Snapshot.CollectionProvenance);
    }

    /// <summary>A named collector used only to shape the real descriptor; it is never asked to collect.</summary>
    private sealed class NamedNoOpCollector(string name) : IEvidenceCollector
    {
        public string CollectorName { get; } = name;

        public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            throw new InvalidOperationException("The descriptor must never call CollectAsync.");
    }

    // =================================================================================================
    // Spec 144 — collect and score as independently invokable passes.
    // =================================================================================================

    /// <summary>
    /// THE primary acceptance criterion (spec 144): <c>collect</c> then <c>score</c> produces
    /// byte-identical scores to the combined run over the same inputs and the same as-of instant.
    /// <para>
    /// Two identical fixture worlds on the SAME fixed clock: one runs the combined
    /// <see cref="RadarPipelineRunner"/>, the other runs <see cref="CollectOnlyPipelineRunner"/> and then
    /// <see cref="ScoreOnlyPipelineRunner"/>. The persisted snapshots are compared as RECORDS — i.e. on
    /// every field at once, so a field added later is covered by construction — with only the per-call
    /// minted <c>Guid</c>s normalised away. That is the same deliberate exclusion the spec-139
    /// replay⊆forward tests make: the engine mints those on EVERY call, so two consecutive forward runs
    /// differ in them just as much; they identify a scoring EVENT, not a scoring RESULT. The evidence links
    /// are compared the same way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CollectThenScore_ProducesByteIdenticalScoresToTheCombinedRun()
    {
        var companyId = Guid.NewGuid();
        var options = new PipelineOptions { GenerateReport = true };

        var combined = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            options);
        await SeedCompanyAsync(combined, companyId);

        var split = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            options);
        await SeedCompanyAsync(split, companyId);

        var combinedResult = await combined.Runner.RunAsync(default);

        var collectResult = await split.CollectOnlyRunner.RunAsync(default);
        var scoreResult = await split.ScoreOnlyRunner(options).RunAsync(default);

        // Not vacuous: the combined run really did score this company off real evidence.
        Assert.Equal(1, combinedResult.CompaniesScored);
        Assert.NotEmpty(combined.ScoreStores.Primary.Written);

        // The two passes between them reproduce every counter the combined run reported.
        Assert.Equal(combinedResult.EvidenceCollected, collectResult.EvidenceCollected);
        Assert.Equal(combinedResult.EvidenceNew, collectResult.EvidenceNew);
        Assert.Equal(combinedResult.SignalsExtracted, collectResult.SignalsExtracted);
        Assert.Equal(combinedResult.SignalsApproved, collectResult.SignalsApproved);
        Assert.Equal(combinedResult.CompaniesScored, scoreResult.CompaniesScored);

        var expected = combined.ScoreStores.Primary.Written;
        var actual = split.ScoreStores.Primary.Written;
        Assert.Equal(expected.Count, actual.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(WithoutMintedId(expected[i].Snapshot), WithoutMintedId(actual[i].Snapshot));
            Assert.Equal(
                expected[i].Links.Select(WithoutMintedIds),
                actual[i].Links.Select(WithoutMintedIds));
        }

        // The provenance chain survived the split: the scored snapshot still links to real evidence, and
        // each link's SignalId — the one id normalised away above — really does resolve to a stored signal.
        var links = Assert.Single(actual).Links;
        Assert.NotEmpty(links);
        var storedSignalIds = (await split.Signals.GetByCompanyAsync(companyId, default))
            .Select(s => s.Id)
            .ToHashSet();
        Assert.All(links, l => Assert.Contains(l.SignalId, storedSignalIds));
    }

    /// <summary>
    /// A <c>score</c> pass performs NO collection and NO AI read. Structural, not incidental: the runner is
    /// handed a spy collector and a spy directional (AI) filing source through the shared graph and neither
    /// is ever called, because <see cref="ScoreOnlyPipelineRunner"/> has no dependency that could reach them.
    /// </summary>
    [Fact]
    public async Task ScorePass_InvokesNoCollectorAndPerformsNoAiRead()
    {
        var collector = new CountingCollector(AsResult([BuildFilingCollected()]));
        var ai = new FakeDirectionalFilingSignalSource(_ => MaterialSignal(type: "GuidanceChange"));

        var h = new Harness(
            collector,
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = false },
            directionalFilingSignals: ai);
        await SeedCompanyAsync(h, Guid.NewGuid());

        var result = await h.ScoreOnlyRunner(new PipelineOptions { GenerateReport = false }).RunAsync(default);

        Assert.Equal(0, collector.CollectCallCount);
        Assert.Equal(0, ai.CallCount);

        // …and it really did score (so the assertions above are not green because nothing happened).
        Assert.Equal(1, result.CompaniesScored);
        Assert.Single(h.ScoreStores.Primary.Written);

        // Every collection counter is honestly zero, and the run record names no collector.
        Assert.Equal(0, result.EvidenceCollected);
        Assert.Equal(0, result.SignalsExtracted);
        Assert.Equal(0, result.SourcesChecked);
        Assert.Empty(Assert.Single(h.RunStore.Written).Collectors);

        // Spec 169: a score pass observed nothing, so it records NO per-collector coverage. Null is the
        // record's "not recorded" value and reads downstream as UNPROVEN — an empty list would claim that
        // zero collectors ran cleanly, which would let a score pass certify a coverage checkpoint.
        Assert.Null(Assert.Single(h.RunStore.Written).CollectorRuns);
    }

    /// <summary>
    /// The structural guarantee behind the test above, asserted directly so it survives future edits: the
    /// standalone score runner takes NO dependency through which collection could happen. A future change
    /// that injects a collector, mapper, extractor, resolver, reviewer, raw-evidence store or AI source into
    /// this type fails here rather than silently re-hitting external APIs on a scheduled scoring run.
    /// </summary>
    [Fact]
    public void ScoreOnlyPipelineRunner_TakesNoCollectionDependency()
    {
        Type[] forbidden =
        [
            typeof(IEvidenceCollector),
            typeof(IEnumerable<IEvidenceCollector>),
            typeof(CollectedEvidenceMapper),
            typeof(ISignalExtractor),
            typeof(ICompanyResolver),
            typeof(ISignalReviewer),
            typeof(IRawEvidenceStore),
            typeof(IDirectionalFilingSignalSource),
            typeof(ICollectionPass),
        ];

        var parameters = typeof(ScoreOnlyPipelineRunner)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        Assert.NotEmpty(parameters);
        foreach (var type in forbidden)
        {
            Assert.DoesNotContain(type, parameters);
        }
    }

    /// <summary>
    /// A PAST-DATED standalone score is a replay, and must not write the live series. It throws pointing at
    /// <c>Radar:Replay:*</c>, and — because the guard runs before anything is loaded or written — leaves no
    /// snapshot, no score-file write, no report and no run record behind.
    /// </summary>
    [Fact]
    public async Task PastDatedScorePass_Throws_AndWritesNothing()
    {
        var h = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = true });
        var companyId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId);

        var runner = h.ScoreOnlyRunner(
            new PipelineOptions { GenerateReport = true }, asOfUtc: FixedNow.AddDays(-1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(default));
        Assert.Contains("Radar:Replay", ex.Message, StringComparison.Ordinal);

        Assert.Empty(h.ScoreStores.Primary.Written);
        Assert.Empty(await h.Scores.GetSnapshotsForCompanyAsync(companyId, default));
        Assert.Empty(h.ReportWriter.Written);
        Assert.Empty(h.RunStore.Written);
    }

    /// <summary>
    /// An as-of instant equal to "now" is NOT past-dated — the boundary is inclusive, so the ordinary
    /// explicitly-pinned-to-this-instant case still runs. (A future instant is likewise allowed: it is not a
    /// replay, and the spec-136 known-at predicate simply includes everything Radar knows.)
    /// </summary>
    [Fact]
    public async Task ScorePassAtExactlyNow_IsAllowed()
    {
        var h = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, Guid.NewGuid());

        var result = await h
            .ScoreOnlyRunner(new PipelineOptions { GenerateReport = false }, asOfUtc: FixedNow)
            .RunAsync(default);

        Assert.Equal(1, result.CompaniesScored);
        Assert.Equal(FixedNow, Assert.Single(h.ScoreStores.Primary.Written).Snapshot.WindowEndUtc);
    }

    /// <summary>
    /// An UNCONFIGURED as-of ("now") must never trip its own past-date guard on an advancing clock. The
    /// runner takes exactly ONE <c>GetUtcNow()</c> and feeds it to both the guard's "now" and the <c>??</c>
    /// default; two reads — with the default resolved from the earlier one — would make <c>asOf &lt; now</c>
    /// on any real clock and turn every unconfigured score pass into a hard failure. Asserted here on the
    /// harness's advancing clock (every read is strictly later than the last), which is what a production
    /// wall clock does.
    /// </summary>
    [Fact]
    public async Task ScorePassWithNoConfiguredAsOf_DoesNotTripItsOwnPastDateGuard_OnAnAdvancingClock()
    {
        var advancing = new AdvancingTimeProvider(FixedNow, TimeSpan.FromMilliseconds(200));

        var h = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = false },
            timeProvider: advancing);
        await SeedCompanyAsync(h, Guid.NewGuid());

        // asOfUtc null ⇒ "now". No throw, and the pass really scores.
        var result = await h
            .ScoreOnlyRunner(new PipelineOptions { GenerateReport = false }, asOfUtc: null)
            .RunAsync(default);

        Assert.Equal(1, result.CompaniesScored);
        Assert.Single(h.ScoreStores.Primary.Written);
    }

    /// <summary>
    /// A <c>collect</c> pass writes evidence and signals and writes NO score snapshot and NO report. It
    /// still writes the append-only run record, with the scoring fields left unclaimed.
    /// </summary>
    [Fact]
    public async Task CollectPass_WritesEvidenceAndSignals_ButNoScoreAndNoReport()
    {
        var h = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = true });
        var companyId = Guid.NewGuid();
        await SeedCompanyAsync(h, companyId);

        var result = await h.CollectOnlyRunner.RunAsync(default);

        // Stages 1–5 all happened.
        Assert.Equal(1, result.EvidenceNew);
        Assert.Single(h.RawStore.Written);
        Assert.NotEmpty(h.SignalStore.Written);
        Assert.NotEmpty(await h.Signals.GetByCompanyAsync(companyId, default));

        // Stage 6 and 7 did not — even though GenerateReport is true, because a collect pass has no
        // reporting stage at all.
        Assert.Equal(0, result.CompaniesScored);
        Assert.Null(result.ReportId);
        Assert.Empty(h.ScoreStores.Primary.Written);
        Assert.Empty(await h.Scores.GetSnapshotsForCompanyAsync(companyId, default));
        Assert.Empty(h.ReportWriter.Written);

        // The run record is written, names the collectors that ran, and claims no scoring.
        var run = Assert.Single(h.RunStore.Written);
        Assert.NotEmpty(run.Collectors);
        Assert.Equal(0, run.CompaniesScored);
        Assert.Null(run.ReportId);
        Assert.Null(run.Strategies);
        Assert.Null(run.PrimaryStrategy);

        // Spec 161: an UNFILTERED run stamps null — the same value every run record written before the field
        // existed deserializes to, so "no companyFilter recorded" and "whole universe" read identically.
        Assert.Null(run.CompanyFilter);

        // Spec 169: a collect pass DID collect, so it records per-collector provenance exactly as the
        // combined run does — same ICollectionPass, so it cannot drift.
        Assert.NotNull(run.CollectorRuns);
        Assert.Equal(run.Collectors, run.CollectorRuns!.Select(c => c.CollectorName));
    }

    /// <summary>
    /// Spec 161: a company-FILTERED collect pass stamps the canonical ticker list on the run record, so a
    /// partial pass is never mistakable for a full one. Provenance only — nothing else about the pass changes
    /// (the filter itself is applied at the seed source).
    /// </summary>
    [Fact]
    public async Task FilteredCollectPass_StampsTheCanonicalCompanyFilterOnTheRunRecord()
    {
        var h = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, Guid.NewGuid());

        // Canonicalisation (trim + upper + de-dupe, configured order) happens in CompanyFilter; the run
        // record must carry that canonical form, not the raw configured tokens.
        var filter = CompanyFilter.FromTickers([" cass ", "IDT", "Cass"]);

        var result = await h.FilteredCollectOnlyRunner(filter).RunAsync(default);

        Assert.Equal(1, result.EvidenceNew);

        var run = Assert.Single(h.RunStore.Written);
        Assert.Equal(["CASS", "IDT"], run.CompanyFilter);
        Assert.Equal(0, run.CompaniesScored);
        Assert.Null(run.Strategies);

        // Spec 169: a filtered pass still records its coverage TRUTHFULLY — it is rejected as a
        // primary-screen checkpoint on CompanyFilter, not by pretending it collected nothing.
        Assert.NotNull(run.CollectorRuns);
    }

    /// <summary>
    /// The spec-141 tripwire still guards a standalone pass — both of them. A strategy edited in place fails
    /// the collect pass before any collection happens AND fails the score pass before any snapshot lands.
    /// </summary>
    [Fact]
    public async Task StandalonePasses_StillFailFastOnAnEditedStrategyIdentity()
    {
        var collector = new CountingCollector(AsResult([BuildCollected()]));
        var configStore = new RecordingScoringConfigStore();
        configStore.StrategyFingerprints[ScoringStrategySet.DefaultStrategyName] = "radar-scoring-fp-stale";

        var h = new Harness(
            collector,
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = false },
            scoringConfigStore: configStore);
        await SeedCompanyAsync(h, Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.CollectOnlyRunner.RunAsync(default));
        Assert.Equal(0, collector.CollectCallCount);
        Assert.Empty(h.RawStore.Written);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.ScoreOnlyRunner(new PipelineOptions { GenerateReport = false }).RunAsync(default));
        Assert.Empty(h.ScoreStores.Primary.Written);
        Assert.Empty(h.RunStore.Written);
    }

    /// <summary>
    /// A <c>score</c> pass may still build the report (stage 7 is optional, not removed), and it does so with
    /// an EMPTY collection summary — nothing was collected this pass, so the transparency footer must not
    /// claim otherwise.
    /// </summary>
    [Fact]
    public async Task ScorePass_BuildsTheReport_WhenGenerateReportIsTrue()
    {
        var h = new Harness(
            new FakeEvidenceCollector([BuildCollected()]),
            new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary")),
            new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, Guid.NewGuid());

        // Accrue something to score first.
        await h.CollectOnlyRunner.RunAsync(default);
        h.RunStore.Written.Clear();

        var result = await h
            .ScoreOnlyRunner(new PipelineOptions { GenerateReport = true })
            .RunAsync(default);

        Assert.NotNull(result.ReportId);
        Assert.Equal(result.ReportId, Assert.Single(h.ReportWriter.Written).Id);
        Assert.Equal(CollectionSummary.Empty, result.Collection);

        var run = Assert.Single(h.RunStore.Written);
        Assert.Equal(result.ReportId, run.ReportId);
        Assert.Equal([ScoringStrategySet.DefaultStrategyName], run.Strategies);
        Assert.Equal(ScoringStrategySet.DefaultStrategyName, run.PrimaryStrategy);
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 179 §2 — the additive in-process transport for the news-risk shadow step.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_ReturnsTheExactDurableRunId_ItWroteToThePipelineRunRecord()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));
        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, Guid.NewGuid());

        var result = await h.Runner.RunAsync(default);

        // ONE run id, minted once: the returned value IS the id of the durable run record on disk, so a
        // shadow assessment's RunId always dereferences to a record that exists.
        var run = Assert.Single(h.RunStore.Written);
        Assert.NotNull(result.RunId);
        Assert.Equal(run.Id, result.RunId);
    }

    [Fact]
    public async Task Run_WithASingleStrategy_ReturnsNullStrategySections()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));
        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = true });
        await SeedCompanyAsync(h, Guid.NewGuid());

        var result = await h.Runner.RunAsync(default);

        // Single strategy ⇒ the builder builds no sections ⇒ the transport carries exactly that null
        // (the NoLiveStrategySections diagnostic downstream, never invented rows).
        Assert.Null(result.StrategySections);
    }

    [Fact]
    public async Task Run_WithGenerateReportFalse_ReturnsNullStrategySections()
    {
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));
        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = false },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, Guid.NewGuid());

        var result = await h.Runner.RunAsync(default);

        Assert.Null(result.StrategySections);
    }

    [Fact]
    public async Task Run_WithTwoStrategies_ReturnsTheReportBuildersSections_TracingToThisRunsOwnSnapshots()
    {
        var companyId = Guid.NewGuid();
        var collector = new FakeEvidenceCollector([BuildCollected()]);
        var extractor = new AnyEvidenceSignalExtractor(new([MaterialSignal()], "summary"));
        var h = new Harness(
            collector, extractor, new PipelineOptions { GenerateReport = true },
            strategies: TwoStrategies());
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        // The structured sections came back through the transport (never re-read, never re-ranked): the
        // primary section's row references the EXACT snapshot id this run's primary store persisted.
        Assert.NotNull(result.StrategySections);
        Assert.Equal(["baseline", "low-media"], result.StrategySections!.Select(s => s.StrategyName));
        var primarySection = result.StrategySections[0];
        Assert.True(primarySection.IsPrimary);
        var row = Assert.Single(primarySection.Rows);
        Assert.Equal(companyId, row.CompanyId);
        Assert.Equal(Assert.Single(h.ScoreStores.Primary.Written).Snapshot.Id, row.ScoreSnapshotId);
    }

    /// <summary>
    /// Normalises the per-call minted <c>Guid</c> out of a snapshot so two runs can be compared as RECORDS on
    /// every other field. Same deliberate exclusion the replay⊆forward tests make.
    /// </summary>
    private static Radar.Domain.Scoring.CompanyScoreSnapshot WithoutMintedId(
        Radar.Domain.Scoring.CompanyScoreSnapshot snapshot) => snapshot with { Id = Guid.Empty };

    /// <summary>
    /// The link equivalent of <see cref="WithoutMintedId"/>. <c>SignalId</c> is normalised too, for the SAME
    /// reason: <c>ExtractedSignalMapper</c> mints a fresh <c>Signal.Id</c> on every extraction, so two
    /// independent collection passes over identical content mint different signal ids — exactly as two
    /// consecutive combined runs do. The link's CONTENT-derived identity, <c>EvidenceId</c> (spec 145), is
    /// compared verbatim, as are the contribution reason and weight; the caller additionally asserts that
    /// each normalised <c>SignalId</c> resolves to a stored signal, so the chain is checked rather than
    /// waved through.
    /// </summary>
    private static Radar.Domain.Scoring.ScoreEvidenceLink WithoutMintedIds(
        Radar.Domain.Scoring.ScoreEvidenceLink link) =>
        link with { Id = Guid.Empty, ScoreSnapshotId = Guid.Empty, SignalId = Guid.Empty };
}
