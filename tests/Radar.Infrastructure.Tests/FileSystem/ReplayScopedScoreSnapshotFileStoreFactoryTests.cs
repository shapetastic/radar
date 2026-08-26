using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Scoring;
using Radar.Domain.Scoring;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 139 — the replay-scoped snapshot store layout: labelled, strategy-scoped, rooted OUTSIDE the live
/// scores directory, and named by as-of instant so a re-run overwrites rather than accumulates.
/// </summary>
public sealed class ReplayScopedScoreSnapshotFileStoreFactoryTests : IDisposable
{
    private static readonly DateTimeOffset WindowStart = new(2026, 5, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public ReplayScopedScoreSnapshotFileStoreFactoryTests()
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
            // Best-effort cleanup.
        }
    }

    private ReplayScopedScoreSnapshotFileStoreFactory CreateFactory() =>
        new(_tempDir, NullLogger<FileScoreSnapshotStore>.Instance);

    private static ScoringStrategyDefinition Strategy(string name, bool isPrimary = false) =>
        new(name, "default", new ScoringWeights(), isPrimary);

    private static CompanyScoreSnapshot Snapshot(Guid companyId, DateTimeOffset windowEnd) =>
        new ScoreSnapshotBuilder()
            .WithCompanyId(companyId)
            .WithWindow(windowEnd - (WindowEnd - WindowStart), windowEnd)
            .WithCreatedAtUtc(windowEnd)
            .Build();

    [Fact]
    public async Task WritesTo_LabelStrategyCompany_NamedByAsOfInstant()
    {
        var companyId = Guid.NewGuid();
        var store = CreateFactory().ForStrategy("my-run", Strategy("broad", isPrimary: true));

        var path = (await store.WriteAsync(Snapshot(companyId, WindowEnd), [], CancellationToken.None)).Path;

        Assert.Equal(
            Path.Combine(_tempDir, "my-run", "strategies", "broad", companyId.ToString(), "20260601T000000Z.json"),
            path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task PrimaryStrategy_IsScopedToo_UnlikeTheLiveFactory()
    {
        // A replay has no legacy location to preserve, so EVERY strategy is uniformly scoped — a consumer
        // never has to know which strategy happened to be primary.
        var companyId = Guid.NewGuid();
        var store = CreateFactory().ForStrategy("run", Strategy("primary-one", isPrimary: true));

        var path = (await store.WriteAsync(Snapshot(companyId, WindowEnd), [], CancellationToken.None)).Path;

        Assert.Contains(Path.Combine("run", "strategies", "primary-one"), path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewritingTheSameAsOf_OverwritesInPlace_SoReplayIsIdempotentOnDisk()
    {
        // The whole reason the file name is as-of-keyed rather than id-keyed: the engine mints a fresh
        // snapshot id per call, so id-named files would accumulate a second copy on every re-run and "two
        // identical replays are diffable to zero" would be false at the file level.
        var companyId = Guid.NewGuid();
        var store = CreateFactory().ForStrategy("run", Strategy("broad"));

        await store.WriteAsync(Snapshot(companyId, WindowEnd), [], CancellationToken.None);
        await store.WriteAsync(Snapshot(companyId, WindowEnd), [], CancellationToken.None);

        var directory = Path.Combine(_tempDir, "run", "strategies", "broad", companyId.ToString());
        Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
    }

    [Fact]
    public async Task DistinctAsOfInstants_ProduceDistinctFiles()
    {
        var companyId = Guid.NewGuid();
        var store = CreateFactory().ForStrategy("run", Strategy("broad"));

        await store.WriteAsync(Snapshot(companyId, WindowEnd), [], CancellationToken.None);
        await store.WriteAsync(Snapshot(companyId, WindowEnd.AddDays(1)), [], CancellationToken.None);

        var directory = Path.Combine(_tempDir, "run", "strategies", "broad", companyId.ToString());
        Assert.Equal(2, Directory.EnumerateFiles(directory, "*.json").Count());
    }

    [Fact]
    public async Task TwoAsOfPointsInsideTheSameSecond_ProduceTwoDistinctFiles()
    {
        // A sub-second step is reachable through config (Radar:Replay:Step accepts a plain TimeSpan string),
        // so two DISTINCT as-of scorings can land inside one second. A second-resolution file name would
        // render both to the same path and silently drop the earlier one while the run still reported both —
        // the exact silent truncation the spec forbids. The name is therefore lossless.
        var companyId = Guid.NewGuid();
        var store = CreateFactory().ForStrategy("run", Strategy("broad"));

        var first = (await store.WriteAsync(Snapshot(companyId, WindowEnd), [], CancellationToken.None)).Path;
        var second = (await store.WriteAsync(
            Snapshot(companyId, WindowEnd.AddMilliseconds(500)), [], CancellationToken.None)).Path;

        Assert.NotEqual(first, second);

        var directory = Path.Combine(_tempDir, "run", "strategies", "broad", companyId.ToString());
        Assert.Equal(2, Directory.EnumerateFiles(directory, "*.json").Count());

        // The whole-second instant keeps the established readable name; only the sub-second one pays for the
        // extra precision.
        Assert.Equal("20260601T000000Z.json", Path.GetFileName(first));
        Assert.Equal("20260601T000000.5000000Z.json", Path.GetFileName(second));
    }

    [Fact]
    public async Task SubSecondNaming_IsStillIdempotent_ForTheSameAsOfInstant()
    {
        // Losslessness must not cost idempotence: re-writing the SAME sub-second instant still overwrites.
        var companyId = Guid.NewGuid();
        var store = CreateFactory().ForStrategy("run", Strategy("broad"));

        await store.WriteAsync(Snapshot(companyId, WindowEnd.AddMilliseconds(500)), [], CancellationToken.None);
        await store.WriteAsync(Snapshot(companyId, WindowEnd.AddMilliseconds(500)), [], CancellationToken.None);

        var directory = Path.Combine(_tempDir, "run", "strategies", "broad", companyId.ToString());
        Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
    }

    [Fact]
    public void RepeatedCalls_ForTheSameLabelAndStrategy_ReturnTheSameStore()
    {
        var factory = CreateFactory();

        Assert.Same(
            factory.ForStrategy("run", Strategy("broad")),
            factory.ForStrategy("run", Strategy("broad")));

        // …and a different label or strategy is a genuinely different store.
        Assert.NotSame(
            factory.ForStrategy("run", Strategy("broad")),
            factory.ForStrategy("other-run", Strategy("broad")));
        Assert.NotSame(
            factory.ForStrategy("run", Strategy("broad")),
            factory.ForStrategy("run", Strategy("narrow")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(" padded")]
    [InlineData("")]
    [InlineData("a\0b")]
    public void UnusableLabel_Throws_BeforeAnyPathIsJoined(string label)
    {
        var factory = CreateFactory();

        Assert.Throws<ArgumentException>(() => factory.ForStrategy(label, Strategy("broad")));
    }

    [Fact]
    public void NulInEitherSegment_Throws_SoTheNulJoinedCacheKeyCannotCollide()
    {
        // The store cache is keyed by "{label}\0{strategy}". That key is collision-free only because NUL is
        // itself a forbidden segment character — without that rule ("a\0b", "c") and ("a", "b\0c") would map
        // onto ONE cache entry and the second pair would silently write into the first pair's directory.
        var factory = CreateFactory();

        Assert.Throws<ArgumentException>(() => factory.ForStrategy("a\0b", Strategy("c")));
        Assert.Throws<ArgumentException>(() => factory.ForStrategy("a", Strategy("b\0c")));
    }
}
