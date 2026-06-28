using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Domain.Evidence;
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

    /// <summary>A deterministic, in-test evidence collector returning a fixed list.</summary>
    private sealed class FakeEvidenceCollector(IReadOnlyList<EvidenceItem> items) : IEvidenceCollector
    {
        public Task<IReadOnlyList<EvidenceItem>> CollectAsync(CancellationToken ct) =>
            Task.FromResult(items);
    }

    /// <summary>A deterministic, in-test extractor returning a fixed output keyed by evidence id.</summary>
    private sealed class FakeSignalExtractor(
        IReadOnlyDictionary<Guid, ExtractSignalsOutput> outputsByEvidenceId) : ISignalExtractor
    {
        public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(
                outputsByEvidenceId.TryGetValue(evidence.Id, out var output)
                    ? output
                    : new ExtractSignalsOutput([], string.Empty));
    }

    private sealed class Harness
    {
        public InMemoryEvidenceRepository Evidence { get; } = new();
        public InMemoryCompanyRepository Companies { get; } = new();
        public InMemorySignalRepository Signals { get; } = new();
        public InMemoryScoreRepository Scores { get; } = new();
        public InMemoryReportRepository Reports { get; } = new();
        public RadarPipelineRunner Runner { get; }

        public Harness(
            IEvidenceCollector collector,
            ISignalExtractor extractor,
            PipelineOptions options)
        {
            var time = new FixedTimeProvider(FixedNow);

            var resolver = new CompanyResolver(Companies, NullLogger<CompanyResolver>.Instance);
            var reviewer = new DeterministicSignalReviewer(
                time, NullLogger<DeterministicSignalReviewer>.Instance);
            var scoringEngine = new ScoringEngine(
                Signals,
                Evidence,
                Scores,
                new RadarScoreFormulaV1(),
                new ScoringOptions(),
                time,
                NullLogger<ScoringEngine>.Instance);
            var reportBuilder = new WeeklyReportBuilder(
                Companies,
                Scores,
                Evidence,
                Signals,
                new WeeklyReportActionPolicyV1(),
                new MarkdownWeeklyReportRenderer(),
                Reports,
                new WeeklyReportOptions(),
                time,
                NullLogger<WeeklyReportBuilder>.Instance);

            Runner = new RadarPipelineRunner(
                collector,
                Evidence,
                extractor,
                resolver,
                reviewer,
                Signals,
                Companies,
                scoringEngine,
                reportBuilder,
                options,
                time,
                NullLogger<RadarPipelineRunner>.Instance);
        }
    }

    private static EvidenceItem BuildEvidence(Guid id, string rawText = RawText, string hash = "hash-nw") =>
        new EvidenceBuilder()
            .WithId(id)
            .WithSourceName("Northwind Newsroom")
            .WithTitle("Northwind Robotics customer win")
            .WithRawText(rawText)
            .WithContentHash(hash)
            .WithPublishedAtUtc(Observed)
            .WithQuality(EvidenceQuality.High)
            .Build();

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
    public async Task HappyPath_FullChain_PersistsAndKeepsProvenance()
    {
        var evidenceId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var evidence = BuildEvidence(evidenceId);
        var collector = new FakeEvidenceCollector([evidence]);
        var extractor = new FakeSignalExtractor(
            new Dictionary<Guid, ExtractSignalsOutput>
            {
                [evidenceId] = new([MaterialSignal()], "summary"),
            });

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

        // Evidence persisted.
        Assert.NotNull(await h.Evidence.GetByIdAsync(evidenceId, default));

        // Exactly one signal persisted, resolved + approved, keeping its evidence id.
        var signals = await h.Signals.GetByCompanyAsync(companyId, default);
        var signal = Assert.Single(signals);
        Assert.Equal(companyId, signal.CompanyId);
        Assert.Equal(SignalReviewStatus.Approved, signal.ReviewStatus);
        Assert.Equal(evidenceId, signal.EvidenceId);

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
    public async Task UnresolvedMention_StaysConservative()
    {
        var evidenceId = Guid.NewGuid();
        var evidence = BuildEvidence(evidenceId);
        var collector = new FakeEvidenceCollector([evidence]);
        var extractor = new FakeSignalExtractor(
            new Dictionary<Guid, ExtractSignalsOutput>
            {
                [evidenceId] = new([MaterialSignal()], "summary"),
            });

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
        var evidenceId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var evidence = BuildEvidence(evidenceId);
        var collector = new FakeEvidenceCollector([evidence]);
        var extractor = new FakeSignalExtractor(
            new Dictionary<Guid, ExtractSignalsOutput>
            {
                [evidenceId] = new([MaterialSignal()], "summary"),
            });

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
    public async Task InvalidExtractedSignal_IsDroppedNotPersisted()
    {
        var evidenceId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var evidence = BuildEvidence(evidenceId);
        var collector = new FakeEvidenceCollector([evidence]);

        // Unknown type AND an excerpt not present in the evidence — both make the mapper reject it.
        var invalid = MaterialSignal(type: "NotARealType", excerpt: "this text is absent from evidence");
        var extractor = new FakeSignalExtractor(
            new Dictionary<Guid, ExtractSignalsOutput>
            {
                [evidenceId] = new([invalid], "summary"),
            });

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
        var evidenceId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var evidence = BuildEvidence(evidenceId);
        var collector = new FakeEvidenceCollector([evidence]);
        var extractor = new FakeSignalExtractor(
            new Dictionary<Guid, ExtractSignalsOutput>
            {
                [evidenceId] = new([MaterialSignal()], "summary"),
            });

        var h = new Harness(collector, extractor, new PipelineOptions { GenerateReport = false });
        await SeedCompanyAsync(h, companyId);

        var result = await h.Runner.RunAsync(default);

        Assert.Null(result.ReportId);

        // The company was still scored, but no report exists.
        var snapshots = await h.Scores.GetSnapshotsForCompanyAsync(companyId, default);
        var snapshot = Assert.Single(snapshots);
        Assert.Null(await h.Reports.GetByIdAsync(snapshot.Id, default)); // snapshot id is not a report id
    }

    [Fact]
    public async Task InjectedClock_IsHonoured_NoUtcNowLeak()
    {
        var evidenceId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var evidence = BuildEvidence(evidenceId);
        var collector = new FakeEvidenceCollector([evidence]);
        var extractor = new FakeSignalExtractor(
            new Dictionary<Guid, ExtractSignalsOutput>
            {
                [evidenceId] = new([MaterialSignal()], "summary"),
            });

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
            // Fresh evidence id + content hash per fresh state so each run sees brand-new evidence.
            var evidenceId = Guid.NewGuid();
            var evidence = BuildEvidence(evidenceId, hash: evidenceId.ToString());
            var collector = new FakeEvidenceCollector([evidence]);
            var extractor = new FakeSignalExtractor(
                new Dictionary<Guid, ExtractSignalsOutput>
                {
                    [evidenceId] = new([MaterialSignal()], "summary"),
                });

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
}
