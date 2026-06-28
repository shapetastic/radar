using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;

namespace Radar.Worker.Tests;

public sealed class WorkerTests
{
    private static readonly RadarPipelineResult EmptyResult = new(0, 0, 0, 0, 0, 0, 0, null);

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
    public async Task Cancellation_DoesNotThrowOutOfExecuteAsync()
    {
        var callLog = new List<string>();
        var seeder = new RecordingSeeder(callLog);
        // RunOnce=false so the worker waits on the interval timer; cancelling the token must unwind cleanly.
        var pipeline = new RecordingPipeline(callLog, EmptyResult);
        using var lifetime = new RecordingLifetime();
        var timeProvider = new FakeTimeProvider();

        var worker = new TestableWorker(
            seeder,
            pipeline,
            lifetime,
            new WorkerRunOptions { RunOnce = false, Interval = TimeSpan.FromMinutes(5) },
            timeProvider,
            NullLogger<Worker>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = worker.RunExecuteAsync(cts.Token);

        // Wait until the first run has happened and the worker is awaiting the next tick.
        var spin = new SpinWait();
        while (pipeline.RunCount < 1)
        {
            spin.SpinOnce();
        }

        // Cancelling the stoppingToken must unwind cleanly — the OperationCanceledException is swallowed
        // inside ExecuteAsync and the task completes successfully (does not throw out).
        cts.Cancel();
        await executeTask;

        Assert.True(executeTask.IsCompletedSuccessfully);
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
        var spin = new SpinWait();
        while (pipeline.RunCount < 1)
        {
            spin.SpinOnce();
        }

        // Advance past one interval to trigger the second run.
        timeProvider.Advance(interval);

        await secondRunGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None);

        Assert.True(pipeline.RunCount >= 2, $"expected >= 2 runs, got {pipeline.RunCount}");
    }

    private sealed class TestableWorker(
        ICompanyUniverseSeeder seeder,
        IRadarPipeline pipeline,
        IHostApplicationLifetime lifetime,
        WorkerRunOptions options,
        TimeProvider timeProvider,
        ILogger<Worker> logger)
        : Worker(seeder, pipeline, lifetime, options, timeProvider, logger)
    {
        public Task RunExecuteAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
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
