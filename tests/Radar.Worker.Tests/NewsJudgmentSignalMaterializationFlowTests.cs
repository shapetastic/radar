using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;

namespace Radar.Worker.Tests;

/// <summary>
/// SPEC 194 §1.2 — the Worker's step ORDER around the judgment-signal materializer: it runs immediately
/// after the judgment pass (whose result it consumes) and BEFORE the news-risk shadow (whose live artifact
/// renders its summary), and it receives the EXACT typing-result instance the judge consumed.
/// <para>
/// The order is the whole correction. Materializing before the judge would reproduce spec 191's defect —
/// a direction taken from a verdict produced by earlier articles — and materializing after the shadow would
/// leave the artifact reporting a pass that had not happened.
/// </para>
/// </summary>
public sealed class NewsJudgmentSignalMaterializationFlowTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static readonly RadarPipelineResult Result =
        new(0, 0, 0, 0, 0, 0, 0, null, 0, 0, CollectionSummary.Empty, RunId, StrategySections: []);

    [Fact]
    public async Task TheMaterializerRunsBetweenTheJudgeAndTheShadow_AndItsSummaryReachesTheArtifact()
    {
        var log = new List<string>();
        var typing = TypingResult();
        var judgmentResult = JudgmentResult();
        var summary = new NewsJudgmentSignalMaterializationSummary(
            JudgmentsConsidered: 3,
            Eligible: 1,
            Materialized: 1,
            AlreadyMaterialized: 0,
            ValidationRejected: 0,
            WriteFailed: 0,
            Skips: new Dictionary<NewsJudgmentSignalSkipReason, int>());

        var materializer = new RecordingMaterializer(log, summary);
        var shadow = new RecordingShadow(log);

        await RunWorkerAsync(
            new RecordingTyping(log, typing),
            new RecordingJudgment(log, judgmentResult),
            materializer,
            shadow);

        Assert.Equal(["typing", "judgment", "materialize", "shadow"], log);

        // It received the SAME instances: the judgment pass's own result and the exact typing result the
        // judge consumed — never a re-read of either store.
        Assert.Same(judgmentResult, materializer.ReceivedJudgment);
        Assert.Same(typing, materializer.ReceivedTyping);

        // The summary rides the judgment result into the shadow, which renders it.
        Assert.NotNull(shadow.ReceivedJudgment);
        Assert.Same(summary, shadow.ReceivedJudgment!.SignalMaterialization);
    }

    [Fact]
    public async Task NoMaterializerRegistered_LeavesTheSummaryNull_MeaningNotAttempted()
    {
        // `null` is NOT "attempted and produced nothing" (that is an all-zero summary) — it is "the step did
        // not run", which is what every pre-194 composition and every judgment-disabled run must report.
        var log = new List<string>();
        var shadow = new RecordingShadow(log);

        await RunWorkerAsync(
            new RecordingTyping(log, TypingResult()),
            new RecordingJudgment(log, JudgmentResult()),
            materializer: null,
            shadow);

        Assert.Equal(["typing", "judgment", "shadow"], log);
        Assert.NotNull(shadow.ReceivedJudgment);
        Assert.Null(shadow.ReceivedJudgment!.SignalMaterialization);
    }

    [Fact]
    public async Task AMaterializerFailure_DoesNotAbortTheRun_AndClaimsNoSummary()
    {
        var log = new List<string>();
        var shadow = new RecordingShadow(log);

        await RunWorkerAsync(
            new RecordingTyping(log, TypingResult()),
            new RecordingJudgment(log, JudgmentResult()),
            new ThrowingMaterializer(log),
            shadow);

        // The shadow still runs, and the judgment result reaches it with NO fabricated summary.
        Assert.Equal(["typing", "judgment", "materialize", "shadow"], log);
        Assert.Null(shadow.ReceivedJudgment!.SignalMaterialization);
    }

    [Fact]
    public async Task NoJudgmentResult_SkipsMaterializationEntirely()
    {
        var log = new List<string>();
        var materializer = new RecordingMaterializer(
            log, NewsJudgmentSignalMaterializationSummary.Empty);

        await RunWorkerAsync(
            new RecordingTyping(log, TypingResult()),
            new RecordingJudgment(log, result: null),
            materializer,
            new RecordingShadow(log));

        Assert.Equal(["typing", "judgment", "shadow"], log);
        Assert.Null(materializer.ReceivedJudgment);
    }

    private static NewsTypingRunResult TypingResult() => new(
        RunId,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddDays(30),
        NewsObservationBatchId: null,
        Cohorts: []);

    private static NewsJudgmentRunResult JudgmentResult() => new(
        Judgments: [],
        Markers: null,
        Stage1FactsDroppedByCohort: new Dictionary<string, int>());

    private static async Task RunWorkerAsync(
        INewsTypingGenerator typing,
        INewsJudgmentGenerator judgment,
        INewsJudgmentSignalMaterializer? materializer,
        INewsRiskShadowGenerator shadow)
    {
        using var lifetime = new NoopLifetime();
        var worker = new Worker(
            new StubSeeder(),
            new StubPipeline(),
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            newsRiskShadowGenerator: shadow,
            newsTypingGenerator: typing,
            newsJudgmentGenerator: judgment,
            newsJudgmentSignalMaterializer: materializer);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;
    }

    private sealed class StubSeeder : ICompanyUniverseSeeder
    {
        public Task<int> SeedAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class StubPipeline : IRadarPipeline
    {
        public Task<RadarPipelineResult> RunAsync(CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class RecordingTyping(List<string> log, NewsTypingRunResult? result)
        : INewsTypingGenerator
    {
        public Task<NewsTypingRunResult?> GenerateAsync(
            Guid? runId, CancellationToken ct, NewsJudgmentCandidatePlan? candidatePlan = null)
        {
            log.Add("typing");
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingJudgment(List<string> log, NewsJudgmentRunResult? result)
        : INewsJudgmentGenerator
    {
        public Task<NewsJudgmentRunResult?> GenerateAsync(
            Guid? runId,
            NewsJudgmentCandidatePlan? candidatePlan,
            NewsTypingRunResult? typing,
            CancellationToken ct)
        {
            log.Add("judgment");
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingMaterializer(
        List<string> log, NewsJudgmentSignalMaterializationSummary summary)
        : INewsJudgmentSignalMaterializer
    {
        public NewsJudgmentRunResult? ReceivedJudgment { get; private set; }

        public NewsTypingRunResult? ReceivedTyping { get; private set; }

        public Task<NewsJudgmentSignalMaterializationSummary> MaterializeAsync(
            NewsJudgmentRunResult judgment, NewsTypingRunResult typing, CancellationToken ct)
        {
            log.Add("materialize");
            ReceivedJudgment = judgment;
            ReceivedTyping = typing;
            return Task.FromResult(summary);
        }
    }

    private sealed class ThrowingMaterializer(List<string> log) : INewsJudgmentSignalMaterializer
    {
        public Task<NewsJudgmentSignalMaterializationSummary> MaterializeAsync(
            NewsJudgmentRunResult judgment, NewsTypingRunResult typing, CancellationToken ct)
        {
            log.Add("materialize");
            throw new InvalidOperationException("Simulated materializer failure.");
        }
    }

    private sealed class RecordingShadow(List<string> log) : INewsRiskShadowGenerator
    {
        public NewsJudgmentRunResult? ReceivedJudgment { get; private set; }

        public Task GenerateAsync(
            Guid? runId,
            IReadOnlyList<StrategyReportSection>? strategySections,
            CancellationToken ct,
            NewsJudgmentRunResult? judgment = null)
        {
            log.Add("shadow");
            ReceivedJudgment = judgment;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _cts.Token;

        public CancellationToken ApplicationStopped => _cts.Token;

        public void Dispose() => _cts.Dispose();

        public void StopApplication() => _cts.Cancel();
    }
}
