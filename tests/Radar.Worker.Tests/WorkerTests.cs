using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Radar.Application.Collectors;
using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Application.Prices;
using Radar.Application.Replay;

namespace Radar.Worker.Tests;

public sealed class WorkerTests
{
    private static readonly RadarPipelineResult EmptyResult =
        new(0, 0, 0, 0, 0, 0, 0, null, 0, 0, CollectionSummary.Empty);

    [Fact]
    public async Task RunOnce_SeedsBeforePipeline_RunsOnce_AndStopsApplication()
    {
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        var pipeline = new RecordingPipeline(callLog, EmptyResult);
        using var lifetime = new RecordingLifetime();
        var timeProvider = new FakeTimeProvider();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            timeProvider,
            NullLogger<Worker>.Instance);

        var stoppingTriggered = false;
        lifetime.ApplicationStopping.Register(() => stoppingTriggered = true);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(1, seeder.SeedCount);
        Assert.Equal(1, pipeline.RunCount);
        Assert.Equal(["seed", "run"], callLog);
        Assert.True(stoppingTriggered);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task RunCompleted_LogIncludesSourcesCheckedAndFailed()
    {
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        var result = new RadarPipelineResult(
            EvidenceCollected: 3,
            EvidenceNew: 2,
            SignalsExtracted: 1,
            SignalsValid: 1,
            SignalsApproved: 1,
            SignalsNeedingReview: 0,
            CompaniesScored: 1,
            ReportId: null,
            SourcesChecked: 5,
            SourcesFailed: 2,
            Collection: CollectionSummary.Empty);
        var pipeline = new RecordingPipeline(callLog, result);
        using var lifetime = new RecordingLifetime();
        var timeProvider = new FakeTimeProvider();
        var logger = new CapturingLogger<Worker>();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            timeProvider,
            logger);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        var completed = Assert.Single(
            logger.Entries, e => e.Message.Contains("pipeline run completed"));
        Assert.Contains("2/5 sources unreadable", completed.Message);
    }

    [Fact]
    public async Task Cancellation_DoesNotThrowOutOfExecuteAsync()
    {
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        // RunOnce=false so the worker waits on the interval timer; cancelling the token must unwind cleanly.
        var pipeline = new RecordingPipeline(callLog, EmptyResult);
        using var lifetime = new RecordingLifetime();
        var timeProvider = new FakeTimeProvider();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = false, Interval = TimeSpan.FromMinutes(5) },
            timeProvider,
            NullLogger<Worker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        // Wait until the first run has happened and the worker is awaiting the next tick.
        // Bounded so a startup regression fails fast instead of hanging the test in CI.
        Assert.True(
            SpinWait.SpinUntil(() => pipeline.RunCount >= 1, TimeSpan.FromSeconds(5)),
            "worker did not reach the first pipeline run within the timeout");

        // Stopping the host cancels the stoppingToken; the OperationCanceledException is swallowed
        // inside ExecuteAsync and the background task completes successfully (does not throw out).
        await worker.StopAsync(CancellationToken.None);

        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task IntervalMode_AdvancingPastInterval_RunsAtLeastTwice()
    {
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        var interval = TimeSpan.FromMinutes(10);
        var secondRunGate = new TaskCompletionSource();
        var pipeline = new RecordingPipeline(callLog, EmptyResult, onRun: count =>
        {
            if (count >= 2)
            {
                secondRunGate.TrySetResult();
            }
        });
        using var lifetime = new RecordingLifetime();
        var timeProvider = new FakeTimeProvider();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = false, Interval = interval },
            timeProvider,
            NullLogger<Worker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        // Wait until the first (immediate) run has occurred and the worker is awaiting the next tick.
        // Bounded so a startup regression fails fast instead of hanging the test in CI.
        Assert.True(
            SpinWait.SpinUntil(() => pipeline.RunCount >= 1, TimeSpan.FromSeconds(5)),
            "worker did not reach the first pipeline run within the timeout");

        // Advance past one interval to trigger the second run.
        timeProvider.Advance(interval);

        await secondRunGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None);

        Assert.True(pipeline.RunCount >= 2, $"expected >= 2 runs, got {pipeline.RunCount}");
    }

    [Fact]
    public async Task ReplayRunner_Present_ReplacesThePipelineRun_AndSkipsPriceAndEfficacy()
    {
        // Spec 139: replay is a read-only OFFLINE mode. It seeds (it needs the company universe) and then
        // runs the replay INSTEAD of the pipeline — no price acquisition, no pipeline, no efficacy render —
        // then stops the application.
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        var pipeline = new RecordingPipeline(callLog, EmptyResult);
        var replay = new RecordingReplayRunner(callLog);
        var prices = new RecordingPriceAcquirer(callLog);
        var efficacy = new RecordingEfficacyGenerator(callLog);
        using var lifetime = new RecordingLifetime();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            prices,
            efficacy,
            replay);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(["seed", "replay"], callLog);
        Assert.Equal(0, pipeline.RunCount);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task StrategyComparison_RunsAfterThePipelineAndTheEfficacyRender_OutsideThePipeline()
    {
        // Spec 140: the comparison is a Worker step DISTINCT from and OUTSIDE IRadarPipeline (AD-14 read
        // side), invoked after the pipeline run so the freshly-persisted snapshots are in the join, and after
        // the per-company render so the artifacts land together.
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        var pipeline = new RecordingPipeline(callLog, EmptyResult);
        var efficacy = new RecordingEfficacyGenerator(callLog);
        var comparison = new RecordingStrategyComparisonGenerator(callLog);
        using var lifetime = new RecordingLifetime();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            priceHistoryAcquirer: null,
            efficacyReportGenerator: efficacy,
            replayRunner: null,
            strategyComparisonGenerator: comparison);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(["seed", "run", "efficacy", "comparison"], callLog);
    }

    [Fact]
    public async Task StrategyComparison_Absent_LeavesTheWorkerUnchanged()
    {
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        var pipeline = new RecordingPipeline(callLog, EmptyResult);
        var efficacy = new RecordingEfficacyGenerator(callLog);
        using var lifetime = new RecordingLifetime();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            priceHistoryAcquirer: null,
            efficacyReportGenerator: efficacy);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(["seed", "run", "efficacy"], callLog);
    }

    [Fact]
    public async Task CompanyFilteredRun_SkipsTheEfficacyRenderAndTheStrategyLeaderboard()
    {
        // Spec 161: both generators join through ICompanyRepository, which under a filter holds only the
        // named companies — recomputing them here would overwrite whole-universe artifacts (including
        // data/efficacy/strategy-leaderboard.{csv,md}) with a partial view. The pipeline itself still runs:
        // the point of a filtered pass is to COLLECT.
        var callLog = new List<string>();
        using var lifetime = new RecordingLifetime();

        var worker = new Worker(
            new RecordingSeeder(callLog),
            new RecordingPipeline(callLog, EmptyResult),
            lifetime,
            new WorkerRunOptions { RunOnce = true, Mode = RadarRunMode.Collect },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            priceHistoryAcquirer: null,
            efficacyReportGenerator: new RecordingEfficacyGenerator(callLog),
            replayRunner: null,
            strategyComparisonGenerator: new RecordingStrategyComparisonGenerator(callLog),
            companyFilter: CompanyFilter.FromTickers(["CASS"]));

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(["seed", "run"], callLog);
    }

    [Fact]
    public async Task NoCompanyFilter_StillRunsTheEfficacyRenderAndTheStrategyLeaderboard()
    {
        // The unfiltered control for the test above: absence of a filter changes nothing, in any mode.
        var callLog = new List<string>();
        using var lifetime = new RecordingLifetime();

        var worker = new Worker(
            new RecordingSeeder(callLog),
            new RecordingPipeline(callLog, EmptyResult),
            lifetime,
            new WorkerRunOptions { RunOnce = true, Mode = RadarRunMode.Collect },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            priceHistoryAcquirer: null,
            efficacyReportGenerator: new RecordingEfficacyGenerator(callLog),
            replayRunner: null,
            strategyComparisonGenerator: new RecordingStrategyComparisonGenerator(callLog));

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(["seed", "run", "efficacy", "comparison"], callLog);
    }

    [Fact]
    public async Task ReplayRun_SkipsTheStrategyComparisonToo()
    {
        // A replay REPLACES the run (spec 139): it must not render efficacy and must not rank strategies.
        var callLog = new List<string>();
        using var lifetime = new RecordingLifetime();
        var worker = new Worker(
            new RecordingSeeder(callLog),
            new RecordingPipeline(callLog, EmptyResult),
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance,
            new RecordingPriceAcquirer(callLog),
            new RecordingEfficacyGenerator(callLog),
            new RecordingReplayRunner(callLog),
            new RecordingStrategyComparisonGenerator(callLog));

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(["seed", "replay"], callLog);
    }

    [Fact]
    public async Task ReplayRunner_Absent_LeavesTheDefaultWorkerUnchanged()
    {
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        var pipeline = new RecordingPipeline(callLog, EmptyResult);
        using var lifetime = new RecordingLifetime();

        var worker = new Worker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = true },
            new FakeTimeProvider(),
            NullLogger<Worker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.Equal(["seed", "run"], callLog);
    }

    private sealed class RecordingReplayRunner(List<string> callLog) : IReplayRunner
    {
        public Task<ReplayResult> RunAsync(CancellationToken ct)
        {
            lock (callLog)
            {
                callLog.Add("replay");
            }

            return Task.FromResult(new ReplayResult(1, 1, 1));
        }
    }

    private sealed class RecordingPriceAcquirer(List<string> callLog) : IPriceHistoryAcquirer
    {
        public Task AcquireAsync(CancellationToken ct)
        {
            lock (callLog)
            {
                callLog.Add("prices");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStrategyComparisonGenerator(List<string> callLog)
        : IStrategyComparisonReportGenerator
    {
        public Task<StrategyLeaderboard> GenerateAsync(CancellationToken ct)
        {
            lock (callLog)
            {
                callLog.Add("comparison");
            }

            return Task.FromResult(new StrategyLeaderboard(
                StrategiesCompared: 0,
                StrategiesConsidered: 0,
                Rows: [],
                DroppedStrategies: [],
                Windows: new StrategyComparisonWindows(0, 0, 0, null, null, null, null),
                Options: StrategyComparisonOptions.Default));
        }
    }

    private sealed class RecordingEfficacyGenerator(List<string> callLog) : IEfficacyReportGenerator
    {
        public Task GenerateAsync(CancellationToken ct)
        {
            lock (callLog)
            {
                callLog.Add("efficacy");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSeeder(List<string> callLog) : ICompanyUniverseSeeder
    {
        private int _seedCount;

        public int SeedCount => _seedCount;

        public Task<int> SeedAsync(CancellationToken ct)
        {
            lock (callLog)
            {
                callLog.Add("seed");
            }

            return Task.FromResult(Interlocked.Increment(ref _seedCount));
        }
    }

    private sealed class RecordingPipeline(
        List<string> callLog, RadarPipelineResult result, Action<int>? onRun = null) : IRadarPipeline
    {
        private int _runCount;

        public int RunCount => _runCount;

        public Task<RadarPipelineResult> RunAsync(CancellationToken ct)
        {
            lock (callLog)
            {
                callLog.Add("run");
            }

            var count = Interlocked.Increment(ref _runCount);
            onRun?.Invoke(count);
            return Task.FromResult(result);
        }
    }

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

    private sealed class RecordingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
