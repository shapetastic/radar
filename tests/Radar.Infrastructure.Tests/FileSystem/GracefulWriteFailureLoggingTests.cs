using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Scoring;
using Radar.Application.Storage;
using Radar.Domain.Reports;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// SPEC 195 §1 at the STORE seam, over the real stores and the real
/// <see cref="GracefulFileWriter"/> — not a fake returning <c>Failed</c>.
/// <para>
/// Spec 193 added the pipeline passes' aggregated "N item(s) could not be durably persisted" Warning but
/// left the writer's per-file Warning in place, so a bad disk emitted N detail lines PLUS the aggregate.
/// These tests pin the substitution at the two batch stores, the exact typed outcome that spec 193 relies
/// on, and — decisively — that callers on the default <c>Immediate</c> mode still log their one failure
/// Warning. Suppressing failures anywhere else would turn a graceful degradation into a silent one.
/// </para>
/// <para>
/// For <see cref="FileScoreSnapshotStore"/> the mode is PER-INSTANCE, so the stores here are built the way
/// production builds them — through <c>AddFileScoreStore</c>,
/// <see cref="StrategyScopedScoreSnapshotFileStoreFactory"/> and
/// <see cref="ReplayScopedScoreSnapshotFileStoreFactory"/> — rather than from a hand-rolled options object
/// that happens to carry the expected value. What is pinned is the real wiring, not a restatement of it.
/// </para>
/// </summary>
public sealed class GracefulWriteFailureLoggingTests : IDisposable
{
    private static readonly DateTimeOffset Observed = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public GracefulWriteFailureLoggingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    /// <summary>
    /// N real signal-file failures produce ZERO per-file Warnings from the store, while every one of them
    /// still returns the typed <see cref="DurableWriteOutcome.Failed"/> the pass counts and still lands in
    /// the in-process index the current run reads. The attempted paths stay recoverable at Debug.
    /// </summary>
    [Fact]
    public async Task FileSignalStore_NFailedWrites_LogNoWarnings_ButStillReportFailedAndKeepTheInProcessCopy()
    {
        var logger = new CapturingLogger<FileSignalStore>();
        var store = new FileSignalStore(
            new FileSignalStoreOptions { RootDirectory = await BlockedRootAsync("signals") }, logger);

        var companyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        for (var i = 0; i < 4; i++)
        {
            var signal = new SignalBuilder()
                .WithCompanyId(companyId)
                .WithObservedAtUtc(Observed.AddHours(i))
                .Build();

            var result = await store.WriteAsync(signal, ReviewFor(signal), CancellationToken.None);

            // Spec 193's typed outcome is untouched: the caller still learns nothing reached disk.
            Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        }

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(4, logger.Entries.Count(e => e.Level == LogLevel.Debug));

        // The current run still completes on what it has: every signal is in this process's index.
        ISignalRepository repository = store;
        var retained = await repository.GetByCompanyAsync(companyId, CancellationToken.None);
        Assert.Equal(4, retained.Count);
    }

    /// <summary>
    /// The score-snapshot mirror of the above, over the PRIMARY store exactly as <c>AddFileScoreStore</c>
    /// registers it — the instance <c>ScoringPass</c> writes the primary strategy's snapshots through, and
    /// therefore the one whose failures that pass's aggregate genuinely covers.
    /// </summary>
    [Fact]
    public async Task FileScoreSnapshotStore_TheScoringPassOwnedPrimaryStore_LogsNoWarnings_ButStillReportsFailed()
    {
        var logger = new CapturingLogger<FileScoreSnapshotStore>();
        var store = new ServiceCollection()
            .AddSingleton<ILogger<FileScoreSnapshotStore>>(logger)
            .AddFileScoreStore(await BlockedRootAsync("scores"))
            .BuildServiceProvider()
            .GetRequiredService<IScoreSnapshotFileStore>();

        for (var i = 0; i < 3; i++)
        {
            var snapshot = new ScoreSnapshotBuilder().WithWindow(WindowStart, WindowEnd).Build();
            var result = await store.WriteAsync(snapshot, [], CancellationToken.None);

            Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        }

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(3, logger.Entries.Count(e => e.Level == LogLevel.Debug));
    }

    /// <summary>
    /// The other ScoringPass-owned construction site: a NON-PRIMARY strategy's store, built by
    /// <see cref="StrategyScopedScoreSnapshotFileStoreFactory"/>. The same pass counts its failures, so it
    /// carries the same mode — pinned at the factory rather than trusting the two sites to agree.
    /// </summary>
    [Fact]
    public async Task FileScoreSnapshotStore_AStrategyScopedStore_LogsNoWarnings_ButStillReportsFailed()
    {
        var logger = new CapturingLogger<FileScoreSnapshotStore>();
        var blockedRoot = await BlockedRootAsync("strategy-scores");
        var factory = new StrategyScopedScoreSnapshotFileStoreFactory(
            new FileScoreSnapshotStore(
                new FileScoreSnapshotStoreOptions { RootDirectory = blockedRoot },
                NullLogger<FileScoreSnapshotStore>.Instance),
            new FileScoreSnapshotStoreOptions { RootDirectory = blockedRoot },
            logger);

        var store = factory.ForStrategy(
            new ScoringStrategyDefinition("filings-led", "default", new ScoringWeights(), IsPrimary: false));

        var result = await store.WriteAsync(
            new ScoreSnapshotBuilder().WithWindow(WindowStart, WindowEnd).Build(),
            [],
            CancellationToken.None);

        Assert.Equal(DurableWriteOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    /// <summary>
    /// The SAME store class, built by <see cref="ReplayScopedScoreSnapshotFileStoreFactory"/>, still logs its
    /// per-file failure Warning — the mirror of
    /// <see cref="FileReportWriter_AnUnrelatedDefaultModeCaller_StillLogsItsOneFailureWarning"/>, and the
    /// reason the mode is per-instance rather than class-wide.
    /// <para>
    /// <c>ReplayRunner</c> DISCARDS the <see cref="DurableWriteResult"/>, unconditionally counts the as-of
    /// point as written, and reports "replayed over N as-of point(s)" at Information; its only Warning is
    /// spec 148's OVERWRITE warning, which is a different fact. So this Warning is the ONLY report a failed
    /// replay write has — suppress it and an unwritable replay directory looks like a successful replay,
    /// leaving spec 140's leaderboard free to rank a silently truncated series.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FileScoreSnapshotStore_AReplayScopedStore_StillLogsItsPerFileFailureWarning()
    {
        var logger = new CapturingLogger<FileScoreSnapshotStore>();
        var factory = new ReplayScopedScoreSnapshotFileStoreFactory(
            await BlockedRootAsync("replays"), logger);

        var store = factory.ForStrategy(
            "asof-2026-06-24",
            new ScoringStrategyDefinition("baseline", "default", new ScoringWeights(), IsPrimary: true));

        await store.WriteAsync(
            new ScoreSnapshotBuilder().WithWindow(WindowStart, WindowEnd).Build(),
            [],
            CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Failed to write file", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(warning.Exception);
    }

    /// <summary>
    /// The other side of the rule: an UNRELATED <see cref="GracefulFileWriter"/> consumer with no aggregate
    /// of its own keeps the default <see cref="GracefulFileWriteFailureLogging.Immediate"/> mode and still
    /// logs its one failure Warning. Spec 195 §1 moves the report; it never removes it.
    /// </summary>
    [Fact]
    public async Task FileReportWriter_AnUnrelatedDefaultModeCaller_StillLogsItsOneFailureWarning()
    {
        var logger = new CapturingLogger<FileReportWriter>();
        var writer = new FileReportWriter(
            new FileReportWriterOptions { RootDirectory = await BlockedRootAsync("reports") }, logger);

        var report = new RadarReport(
            Id: Guid.NewGuid(),
            ReportType: "weekly",
            Title: "Radar weekly",
            PeriodStartUtc: WindowStart,
            PeriodEndUtc: WindowEnd,
            MarkdownContent: "# Weekly\n",
            CreatedAtUtc: WindowEnd);

        await writer.WriteAsync(report, CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Failed to write file", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(warning.Exception);
    }

    /// <summary>
    /// A root that is an existing FILE, so every <c>Directory.CreateDirectory</c> beneath it throws
    /// <see cref="IOException"/> — the established mechanism from <c>GracefulFileWriterTests</c>, reused
    /// rather than reinvented so both files force failure the same way.
    /// </summary>
    private async Task<string> BlockedRootAsync(string name)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, "not a directory");
        return path;
    }

    private static SignalReview ReviewFor(Signal signal) => new(
        Id: Guid.NewGuid(),
        SignalId: signal.Id,
        ReviewerName: "test-reviewer",
        Decision: SignalReviewDecision.Approve,
        Summary: "Approved by the fixture.",
        IssuesJson: null,
        ReviewedAtUtc: signal.CreatedAtUtc);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
