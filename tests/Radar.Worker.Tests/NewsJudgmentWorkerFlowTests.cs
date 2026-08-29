using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.TestSupport;

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

    private sealed class StubPipeline(RadarPipelineResult? result = null) : IRadarPipeline
    {
        public Task<RadarPipelineResult> RunAsync(CancellationToken ct) =>
            Task.FromResult(result ?? Result);
    }

    private sealed class RecordingTyping(List<string> log, NewsTypingRunResult? result) : INewsTypingGenerator
    {
        /// <summary>Spec 187 §2: the EXACT plan instance the Worker handed the typing pass.</summary>
        public NewsJudgmentCandidatePlan? ReceivedPlan { get; private set; }

        public Task<NewsTypingRunResult?> GenerateAsync(
            Guid? runId, CancellationToken ct, NewsJudgmentCandidatePlan? candidatePlan = null)
        {
            log.Add("typing");
            ReceivedPlan = candidatePlan;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingJudgment(List<string> log, NewsJudgmentRunResult? result)
        : INewsJudgmentGenerator
    {
        public NewsTypingRunResult? ReceivedTyping { get; private set; }

        /// <summary>Spec 187 §2: the EXACT plan instance the Worker handed the judge.</summary>
        public NewsJudgmentCandidatePlan? ReceivedPlan { get; private set; }

        public Task<NewsJudgmentRunResult?> GenerateAsync(
            Guid? runId,
            NewsJudgmentCandidatePlan? candidatePlan,
            NewsTypingRunResult? typing,
            CancellationToken ct)
        {
            log.Add("judgment");
            ReceivedTyping = typing;
            ReceivedPlan = candidatePlan;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingShadow(List<string> log) : INewsRiskShadowGenerator
    {
        public NewsJudgmentRunResult? ReceivedJudgment { get; private set; }

        public Task<NewsRiskShadowRunResult> GenerateAsync(
            Guid? runId,
            IReadOnlyList<StrategyReportSection>? strategySections,
            CancellationToken ct,
            NewsJudgmentRunResult? judgment = null)
        {
            log.Add("shadow");
            ReceivedJudgment = judgment;
            return Task.FromResult(NewsRiskShadowRunResult.NoWriteAttempted);
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

    private static StrategyReportRow Row(int rank, Guid companyId, string name, string ticker) => new(
        Rank: rank,
        CompanyId: companyId,
        CompanyName: name,
        Ticker: ticker,
        ScoreSnapshotId: Guid.NewGuid(),
        Snapshot: new ScoreSnapshotBuilder().WithCompanyId(companyId).Build());

    private static NewsJudgmentRunResult JudgmentResult(NewsJudgmentMarkerReportModel? markers) => new(
        Judgments: [],
        Markers: markers,
        Stage1FactsDroppedByCohort: new Dictionary<string, int>());

    private static async Task RunWorkerAsync(
        RecordingTyping? typing,
        RecordingJudgment? judgment,
        RecordingShadow? shadow,
        RecordingRerenderer? rerenderer,
        INewsJudgmentCandidatePlanner? candidatePlanner = null,
        RadarPipelineResult? pipelineResult = null)
    {
        using var lifetime = new NoopDisposableLifetime();
        var worker = new Worker(
            new StubSeeder(),
            new StubPipeline(pipelineResult),
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            newsRiskShadowGenerator: shadow,
            newsTypingGenerator: typing,
            newsJudgmentGenerator: judgment,
            judgmentRerenderer: rerenderer,
            candidatePlanner: candidatePlanner);

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

    /// <summary>
    /// Spec 187 §2: the Worker computes the ordered candidate plan ONCE and hands the SAME immutable
    /// instance to typing and to the judge. Reference identity is the assertion, because "two passes over
    /// equal-looking lists" is precisely the arrangement that let the first live run type one set of
    /// companies and judge another.
    /// </summary>
    [Fact]
    public async Task TheCandidatePlan_IsComputedOnce_AndTheSameInstanceReachesTypingAndTheJudge()
    {
        var alpha = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var beta = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var sections = new List<StrategyReportSection>
        {
            new(
                StrategyName: "disclosure-led-v11",
                FormulaVersion: "radar-formula-v8",
                ScoringConfigVersion: "radar-scoring-fp-test",
                IsPrimary: true,
                CompaniesScored: 2,
                CompaniesWithLinkedEvidence: 2,
                Rows: [Row(1, alpha, "Alpha Co", "ALPH"), Row(2, beta, "Beta Co", "BETA")])
            {
                Purpose = StrategyPurpose.Research,
            },
        };
        var planner = new NewsJudgmentCandidatePlanner(new NewsJudgmentOptions(
            outputDirectory: "unused",
            maxCompaniesPerRun: 30,
            maxFamiliesPerJudgment: 50,
            maxJudgmentAttempts: 3,
            presentationJudge: "judge",
            presentationExtractor: "extractor",
            newsSearchCollectorName: "newssearch"));

        var log = new List<string>();
        var typing = new RecordingTyping(log, TypingResult());
        var judgment = new RecordingJudgment(log, JudgmentResult(markers: null));

        await RunWorkerAsync(
            typing,
            judgment,
            shadow: null,
            rerenderer: null,
            candidatePlanner: planner,
            pipelineResult: Result with { StrategySections = sections });

        Assert.NotNull(typing.ReceivedPlan);
        Assert.Same(typing.ReceivedPlan, judgment.ReceivedPlan);
        Assert.Equal([alpha, beta], typing.ReceivedPlan!.CompanyIds);
    }

    /// <summary>
    /// With judgment disabled the planner is never registered, so typing receives NO plan and its selection
    /// stays byte-identical to the pre-187 §2 behaviour (the candidate capacity is simply unused).
    /// </summary>
    [Fact]
    public async Task WithNoPlanner_TypingReceivesNoCandidatePlan()
    {
        var log = new List<string>();
        var typing = new RecordingTyping(log, TypingResult());

        await RunWorkerAsync(typing, judgment: null, shadow: null, rerenderer: null);

        Assert.Null(typing.ReceivedPlan);
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
