using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.IntegrationTests;

/// <summary>
/// SPEC 195 §1 end to end, over the REAL wired graph, the REAL on-disk stores and the REAL
/// <c>GracefulFileWriter</c> — never a fake store returning <c>Failed</c>.
/// <para>
/// Spec 193 gave <c>CollectionPass</c>/<c>ScoringPass</c> an aggregated "N item(s) could not be durably
/// persisted" Warning but left the writer's per-file Warning in place, so N failed writes produced N detail
/// lines PLUS the aggregate — the aggregate was ADDED, not substituted. These tests assert the substitution
/// where it actually has to hold: zero per-file Warnings from the store's own logger category, exactly ONE
/// aggregated Warning from the pass's category, spec 193's counters still exact, and the run still
/// completing on its in-memory copies.
/// </para>
/// <para>
/// A write is made to fail the same way <c>GracefulFileWriterTests</c> makes one fail: the store's root is
/// an existing FILE, so every <c>Directory.CreateDirectory</c> beneath it throws <see cref="IOException"/>.
/// Nothing about the catch set, the graceful <c>false</c> or the typed outcome is mocked.
/// </para>
/// </summary>
public sealed class FailedWriteAggregationEndToEndTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid NorthwindId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Northwind = "Northwind Robotics";

    private const string SignalStoreCategory = "Radar.Infrastructure.FileSystem.FileSignalStore";
    private const string SnapshotStoreCategory = "Radar.Infrastructure.FileSystem.FileScoreSnapshotStore";
    private const string CollectionPassCategory = "Radar.Application.Pipeline.CollectionPass";
    private const string ScoringPassCategory = "Radar.Application.Pipeline.ScoringPass";

    /// <summary>
    /// Every signal file fails to write. The store logs NO Warning at all (its per-file line is now the
    /// pass's job) while <c>CollectionPass</c> logs exactly one aggregate; <c>SignalsNotPersisted</c> stays
    /// exact and every signal is still in the in-memory repository the current run scored from.
    /// </summary>
    [Fact]
    public async Task FailedSignalWrites_ProduceZeroPerFileWarnings_AndOneCollectionPassAggregate()
    {
        using var fx = new TempPipelineFixtures();
        WriteFixture(fx);

        // Block the signals root: an existing FILE where the store wants a directory.
        await File.WriteAllTextAsync(fx.SignalsDir, "not a directory");

        var logs = new CapturingLoggerProvider();
        await using var sp = BuildProvider(fx, logs);
        await SeedAndRunAsync(sp);
        var record = await ReadRunRecordAsync(sp);

        // N > 1, so "one aggregate" is a real claim about aggregation rather than about a single failure.
        Assert.True(
            record.SignalsNotPersisted >= 2,
            $"Expected at least two failed signal writes; got {record.SignalsNotPersisted}.");

        // spec 193's counter stays EXACT: every signal the pass produced is counted as not persisted.
        Assert.Equal(record.SignalsValid, record.SignalsNotPersisted);

        Assert.Empty(logs.Warnings(SignalStoreCategory));

        var aggregate = Assert.Single(logs.Warnings(CollectionPassCategory));
        Assert.Contains("could NOT be durably persisted", aggregate, StringComparison.Ordinal);
        // The false "see the per-write Warnings above" pointer is gone: those Warnings no longer exist.
        Assert.DoesNotContain("per-write Warnings above", aggregate, StringComparison.Ordinal);

        // The attempted paths stayed recoverable — at Debug, without an exception, in bounded form.
        Assert.Contains(
            logs.Entries, e => e.Category == SignalStoreCategory && e.Level == LogLevel.Debug);

        // Graceful, not silent: the current run still has every signal in memory.
        var repository = sp.GetRequiredService<ISignalRepository>();
        var retained = await repository.GetByCompanyAsync(NorthwindId, CancellationToken.None);
        Assert.NotEmpty(retained);
        Assert.False(Directory.Exists(fx.SignalsDir), "Nothing may have reached disk.");
    }

    /// <summary>The score-snapshot mirror: zero per-file Warnings, one <c>ScoringPass</c> aggregate.</summary>
    [Fact]
    public async Task FailedSnapshotWrites_ProduceZeroPerFileWarnings_AndOneScoringPassAggregate()
    {
        using var fx = new TempPipelineFixtures();
        WriteFixture(fx);

        var scoresDir = Path.Combine(fx.RootDir, "scores");
        await File.WriteAllTextAsync(scoresDir, "not a directory");

        var logs = new CapturingLoggerProvider();
        await using var sp = BuildProvider(fx, logs);
        await SeedAndRunAsync(sp);
        var record = await ReadRunRecordAsync(sp);

        Assert.Equal(1, record.ScoreSnapshotsNotPersisted);

        Assert.Empty(logs.Warnings(SnapshotStoreCategory));

        var aggregate = Assert.Single(logs.Warnings(ScoringPassCategory));
        Assert.Contains("could NOT be durably persisted", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("per-write Warnings above", aggregate, StringComparison.Ordinal);

        Assert.Contains(
            logs.Entries, e => e.Category == SnapshotStoreCategory && e.Level == LogLevel.Debug);

        // The run still reported on the score it holds in memory.
        var scores = sp.GetRequiredService<IScoreRepository>();
        Assert.NotEmpty(await scores.GetSnapshotsForCompanyAsync(NorthwindId, CancellationToken.None));
        Assert.False(Directory.Exists(scoresDir), "Nothing may have reached disk.");
    }

    /// <summary>
    /// The control: with healthy stores nobody logs a failure at all, so the two tests above are measuring
    /// the blocked root rather than an unconditionally silent store.
    /// </summary>
    [Fact]
    public async Task HealthyRun_LogsNoFailureWarningFromEitherTheStoresOrThePasses()
    {
        using var fx = new TempPipelineFixtures();
        WriteFixture(fx);

        var logs = new CapturingLoggerProvider();
        await using var sp = BuildProvider(fx, logs);
        await SeedAndRunAsync(sp);
        var record = await ReadRunRecordAsync(sp);

        Assert.Equal(0, record.SignalsNotPersisted);
        Assert.Equal(0, record.ScoreSnapshotsNotPersisted);

        Assert.Empty(logs.Warnings(SignalStoreCategory));
        Assert.Empty(logs.Warnings(SnapshotStoreCategory));
        Assert.Empty(logs.Warnings(CollectionPassCategory));
        Assert.Empty(logs.Warnings(ScoringPassCategory));
    }

    /// <summary>The durable run record spec 193's counters live on, read back through the real store.</summary>
    private static async Task<PipelineRunRecord> ReadRunRecordAsync(ServiceProvider sp)
    {
        var runs = await sp.GetRequiredService<IPipelineRunStore>()
            .ReadRecentAsync(5, CancellationToken.None);
        return Assert.Single(runs);
    }

    private static ServiceProvider BuildProvider(TempPipelineFixtures fx, CapturingLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedNow));
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(logs);
        });
        services.AddInMemoryRadarPersistence();
        services.AddRadarApplicationServices();
        services.AddLocalFileCollector(fx.EvidenceDir);
        services.AddLocalFileCompanySeed(fx.SeedFilePath);
        services.AddFileRawEvidenceStore(fx.RawEvidenceDir);
        services.AddFileSignalStore(fx.SignalsDir);
        services.AddFileScoreStore(Path.Combine(fx.RootDir, "scores"));
        services.AddFileReportWriter(Path.Combine(fx.RootDir, "reports"));
        services.AddFilePipelineRunStore(Path.Combine(fx.RootDir, "runs"));
        services.AddFileScoringConfigStore(Path.Combine(fx.RootDir, "scoring-configs"));
        services.AddRadarPipeline();
        return services.BuildServiceProvider();
    }

    /// <summary>Two evidence items on distinct dates, so more than one signal write can fail.</summary>
    private static void WriteFixture(TempPipelineFixtures fx)
    {
        fx.WriteCompanies([new(NorthwindId, Northwind, "NWR", [])]);
        fx.WriteEvidence(
            "northwind-launch.json", Northwind, "Northwind launch",
            "Northwind Robotics launches a new platform for industrial automation.",
            "2026-06-24T00:00:00Z", quality: "High");
        fx.WriteEvidence(
            "northwind-win.json", Northwind, "Northwind customer win",
            "Northwind Robotics signs a multi-year deal with a Fortune 100 partner.",
            "2026-06-25T00:00:00Z", quality: "High");
    }

    private static async Task SeedAndRunAsync(ServiceProvider sp)
    {
        await sp.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(default);
        await sp.GetRequiredService<IRadarPipeline>().RunAsync(default);
    }

    /// <summary>
    /// Captures every entry the composed graph emits WITH its category, which is the whole point: "zero
    /// per-file Warnings and exactly one aggregate" is a claim about WHICH logger spoke, and a
    /// category-less capture could not tell the store's line from the pass's.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => [.. _entries];

        public IEnumerable<string> Warnings(string category) => Entries
            .Where(e => e.Category == category && e.Level >= LogLevel.Warning)
            .Select(e => e.Message);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
