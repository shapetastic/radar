using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 185 §5 — the Worker's post-run step ORDER (typing → judgment → shadow, so the judge consumes this
/// run's families and the shadow's artifact embeds this run's judgments), the marker re-render (only after
/// a judgment pass that resolved its presentation cohort), and the honest no-op when the judgment step is
/// not registered.
/// </summary>
public sealed class NewsJudgmentWorkerFlowTests
{
    private static readonly Guid RunId = Guid.NewGuid();

    private static readonly RadarPipelineResult Result =
        new(0, 0, 0, 0, 0, 0, 0, null, 0, 0, CollectionSummary.Empty, RunId, StrategySections: []);

    private sealed class StubSeeder : ICompanyUniverseSeeder
    {
        public Task<int> SeedAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class StubPipeline : IRadarPipeline
    {
        public Task<RadarPipelineResult> RunAsync(CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class RecordingTyping(List<string> log, NewsTypingRunResult? result) : INewsTypingGenerator
    {
        public Task<NewsTypingRunResult?> GenerateAsync(Guid? runId, CancellationToken ct)
        {
            log.Add("typing");
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingJudgment(List<string> log, NewsJudgmentRunResult? result)
        : INewsJudgmentGenerator
    {
        public NewsTypingRunResult? ReceivedTyping { get; private set; }

        public Task<NewsJudgmentRunResult?> GenerateAsync(
            Guid? runId,
            IReadOnlyList<StrategyReportSection>? strategySections,
            NewsTypingRunResult? typing,
            CancellationToken ct)
        {
            log.Add("judgment");
            ReceivedTyping = typing;
            return Task.FromResult(result);
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

    private sealed class RecordingRerenderer(List<string> log) : IWeeklyReportJudgmentRerenderer
    {
        public NewsJudgmentMarkerReportModel? Rendered { get; private set; }

        public void CaptureRendered(WeeklyReportModel model, Radar.Domain.Reports.RadarReport report)
        {
        }

        public Task<bool> RerenderAsync(NewsJudgmentMarkerReportModel markers, CancellationToken ct)
        {
            log.Add("rerender");
            Rendered = markers;
            return Task.FromResult(true);
        }
    }

    private static NewsTypingRunResult TypingResult() => new(
        RunId,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddDays(30),
        NewsObservationBatchId: null,
        Cohorts: []);

    private static NewsJudgmentRunResult JudgmentResult(NewsJudgmentMarkerReportModel? markers) => new(
        Judgments: [],
        Markers: markers,
        Stage1FactsDroppedByCohort: new Dictionary<string, int>());

    private static async Task RunWorkerAsync(
        RecordingTyping? typing,
        RecordingJudgment? judgment,
        RecordingShadow? shadow,
        RecordingRerenderer? rerenderer)
    {
        using var lifetime = new NoopDisposableLifetime();
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
            judgmentRerenderer: rerenderer);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;
    }

    private sealed class NoopDisposableLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _cts.Token;

        public CancellationToken ApplicationStopped => _cts.Token;

        public void StopApplication() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();
    }

    [Fact]
    public async Task Order_IsTypingThenJudgmentThenShadow_AndTheShadowReceivesTheJudgment()
    {
        var log = new List<string>();
        var markers = new NewsJudgmentMarkerReportModel(
            JudgmentPending: false, Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>());
        var typing = new RecordingTyping(log, TypingResult());
        var judgment = new RecordingJudgment(log, JudgmentResult(markers));
        var shadow = new RecordingShadow(log);
        var rerenderer = new RecordingRerenderer(log);

        await RunWorkerAsync(typing, judgment, shadow, rerenderer);

        Assert.Equal(["typing", "judgment", "rerender", "shadow"], log);
        Assert.NotNull(judgment.ReceivedTyping);
        Assert.Same(markers, rerenderer.Rendered);
        Assert.NotNull(shadow.ReceivedJudgment);
        Assert.Same(markers, shadow.ReceivedJudgment!.Markers);
    }

    [Fact]
    public async Task JudgmentAbsent_SkipsTheStepAndTheRerender_ShadowStillRuns()
    {
        var log = new List<string>();
        var typing = new RecordingTyping(log, TypingResult());
        var shadow = new RecordingShadow(log);

        await RunWorkerAsync(typing, judgment: null, shadow, rerenderer: null);

        Assert.Equal(["typing", "shadow"], log);
        Assert.Null(shadow.ReceivedJudgment);
    }

    [Fact]
    public async Task UnresolvedMarkers_NeverRerender_ButTheShadowStillEmbedsTheJudgments()
    {
        var log = new List<string>();
        var typing = new RecordingTyping(log, TypingResult());
        var judgment = new RecordingJudgment(log, JudgmentResult(markers: null));
        var shadow = new RecordingShadow(log);
        var rerenderer = new RecordingRerenderer(log);

        await RunWorkerAsync(typing, judgment, shadow, rerenderer);

        Assert.Equal(["typing", "judgment", "shadow"], log);
        Assert.Null(rerenderer.Rendered);
        Assert.NotNull(shadow.ReceivedJudgment);
    }

    [Fact]
    public async Task TypingFailure_HandsNullToTheJudge_WhichCanReturnNull_AndNothingRerenders()
    {
        var log = new List<string>();
        var typing = new RecordingTyping(log, result: null);
        var judgment = new RecordingJudgment(log, result: null);
        var shadow = new RecordingShadow(log);
        var rerenderer = new RecordingRerenderer(log);

        await RunWorkerAsync(typing, judgment, shadow, rerenderer);

        Assert.Equal(["typing", "judgment", "shadow"], log);
        Assert.Null(judgment.ReceivedTyping);
        Assert.Null(rerenderer.Rendered);
    }
}
