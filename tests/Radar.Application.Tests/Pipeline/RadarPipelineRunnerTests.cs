using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Filings;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
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
        public bool WasCalled { get; private set; }

        public string CollectorName => name;

        public EvidenceSourceType SourceType => type;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            WasCalled = true;
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

        public Task<string> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct)
        {
            Written.Add((signal, review));
            return Task.FromResult("written/signal.json");
        }

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<Signal> result = Written
                .Select(w => w.Signal)
                .Where(s => s.CompanyId == companyId)
                .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
                .Where(s => s.ObservedAtUtc > startExclusiveUtc && s.ObservedAtUtc <= endInclusiveUtc)
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

        public Task<string> WriteAsync(
            Radar.Domain.Scoring.CompanyScoreSnapshot snapshot,
            IReadOnlyList<Radar.Domain.Scoring.ScoreEvidenceLink> links,
            CancellationToken ct)
        {
            Written.Add((snapshot, links));
            return Task.FromResult("written/score.json");
        }

        public Task<Radar.Domain.Scoring.CompanyScoreSnapshot?> ReadLatestBeforeAsync(
            Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct) =>
            Task.FromResult(Written
                .Select(w => w.Snapshot)
                .Where(s => s.CompanyId == companyId && s.CreatedAtUtc < beforeUtc)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ThenByDescending(s => s.Id)
                .FirstOrDefault());
    }

    /// <summary>
    /// A fake <see cref="IReportFileWriter"/> that records every <see cref="RadarReport"/> it is asked
    /// to write and returns a fixed path. Lets tests assert whether (and which) report the runner
    /// wrote to disk.
    /// </summary>
    private sealed class RecordingReportFileWriter : IReportFileWriter
    {
        public List<RadarReport> Written { get; } = new();

        public Task<string> WriteAsync(RadarReport report, CancellationToken ct)
        {
            Written.Add(report);
            return Task.FromResult("written/path.md");
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

        public Task<string> WriteAsync(PipelineRunRecord record, CancellationToken ct)
        {
            Written.Add(record);
            return Task.FromResult("written/run.json");
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
    }

    private sealed class Harness
    {
        public InMemoryEvidenceRepository Evidence { get; } = new();
        public RecordingRawEvidenceStore RawStore { get; } = new();
        public RecordingReportFileWriter ReportWriter { get; } = new();
        public RecordingPipelineRunStore RunStore { get; } = new();
        public InMemoryCompanyRepository Companies { get; } = new();
        public InMemorySignalRepository Signals { get; } = new();
        public InMemorySignalReviewRepository Reviews { get; } = new();
        public RecordingSignalFileStore SignalStore { get; } = new();
        public RecordingScoreSnapshotFileStore ScoreStore { get; } = new();
        public InMemoryScoreRepository Scores { get; } = new();
        public InMemoryReportRepository Reports { get; } = new();
        public RadarPipelineRunner Runner { get; }

        public Harness(
            IEvidenceCollector collector,
            ISignalExtractor extractor,
            PipelineOptions options,
            TimeProvider? timeProvider = null,
            IDirectionalFilingSignalSource? directionalFilingSignals = null)
            : this([collector], extractor, options, timeProvider, directionalFilingSignals)
        {
        }

        public Harness(
            IReadOnlyList<IEvidenceCollector> collectors,
            ISignalExtractor extractor,
            PipelineOptions options,
            TimeProvider? timeProvider = null,
            IDirectionalFilingSignalSource? directionalFilingSignals = null)
        {
            var time = timeProvider ?? new FixedTimeProvider(FixedNow);

            var resolver = new CompanyResolver(Companies, NullLogger<CompanyResolver>.Instance);
            var reviewer = new DeterministicSignalReviewer(
                time, NullLogger<DeterministicSignalReviewer>.Instance);
            var scoringEngine = new ScoringEngine(
                Signals,
                SignalStore,
                Evidence,
                Scores,
                new RadarScoreFormulaV2(),
                new ScoringOptions(),
                NullLogger<ScoringEngine>.Instance);
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
                new WeeklyReportOptions(),
                time,
                NullLogger<WeeklyReportBuilder>.Instance);

            var mapper = new CollectedEvidenceMapper(
                new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance);

            Runner = new RadarPipelineRunner(
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
                scoringEngine,
                ScoreStore,
                reportBuilder,
                ReportWriter,
                RunStore,
                options,
                time,
                NullLogger<RadarPipelineRunner>.Instance,
                directionalFilingSignals);
        }
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
            services.AddLocalFileCollector(tempDir);
            services.AddFileRawEvidenceStore(Path.Combine(tempDir, "raw"));
            services.AddFileSignalStore(Path.Combine(tempDir, "signals"));
            services.AddFileScoreStore(Path.Combine(tempDir, "scores"));
            services.AddFileReportWriter(Path.Combine(tempDir, "reports"));
            services.AddFilePipelineRunStore(Path.Combine(tempDir, "runs"));
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
        // because AAA is processed first under the CollectorName-ordinal order. The mapper assigns a
        // fresh id each map, so the stored item is located by content hash (computed from title+rawText).
        var canonical = new CollectedEvidenceMapper(
            new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance)
            .ToEvidenceItem(aEvidence);
        var stored = await h.Evidence.GetByContentHashAsync(canonical.ContentHash, default);
        Assert.NotNull(stored);
        Assert.Equal(EvidenceSourceType.Filing, stored!.SourceType);
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

    /// <summary>
    /// A fake directional filing signal source that records the candidate evidence it receives and emits
    /// one directional signal per candidate (via a caller-supplied factory) so the runner-threading test
    /// stays decoupled from the real reader/analyzer (those live behind Infrastructure interfaces).
    /// </summary>
    private sealed class FakeDirectionalFilingSignalSource(
        Func<EvidenceItem, ExtractedSignal> signalFor) : IDirectionalFilingSignalSource
    {
        public List<EvidenceItem> ReceivedCandidates { get; } = new();

        public Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
            IReadOnlyList<EvidenceItem> candidateEvidence, DateTimeOffset asOfUtc, CancellationToken ct)
        {
            ReceivedCandidates.AddRange(candidateEvidence);
            IReadOnlyList<DirectionalFilingSignal> produced = candidateEvidence
                .Select(ev => new DirectionalFilingSignal(signalFor(ev), ev))
                .ToList();
            return Task.FromResult(produced);
        }
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
}
