using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Scoring;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// Spec 137 — storage layout. The PRIMARY strategy keeps the existing location so the spec-101/108 efficacy
/// read, the weekly report's "vs previous run" read and all accrued history keep working with no migration;
/// non-primary strategies are scoped under <c>strategies/{name}/</c> so the series can never collide.
/// </summary>
public sealed class StrategyScopedScoreSnapshotFileStoreFactoryTests : IDisposable
{
    private readonly string _tempDir;

    public StrategyScopedScoreSnapshotFileStoreFactoryTests()
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

    private (StrategyScopedScoreSnapshotFileStoreFactory Factory, IScoreSnapshotFileStore Primary) Create()
    {
        var options = new FileScoreSnapshotStoreOptions { RootDirectory = _tempDir };
        var primary = new FileScoreSnapshotStore(options, NullLogger<FileScoreSnapshotStore>.Instance);
        return (
            new StrategyScopedScoreSnapshotFileStoreFactory(
                primary, options, NullLogger<FileScoreSnapshotStore>.Instance),
            primary);
    }

    private static ScoringStrategyDefinition Def(string name, bool primary) =>
        new(name, "default", new ScoringWeights(), primary);

    [Fact]
    public void PrimaryStrategy_GetsTheRegisteredStoreVerbatim()
    {
        var (factory, primary) = Create();

        Assert.Same(primary, factory.ForStrategy(Def("baseline", primary: true)));
    }

    [Fact]
    public void NonPrimaryStrategy_GetsItsOwnCachedStore()
    {
        var (factory, primary) = Create();
        var definition = Def("low-media", primary: false);

        var store = factory.ForStrategy(definition);

        Assert.NotSame(primary, store);
        Assert.Same(store, factory.ForStrategy(definition));
    }

    [Fact]
    public async Task PrimaryWritesToLegacyPath_NonPrimaryWritesUnderTheStrategySegment()
    {
        var (factory, _) = Create();
        var companyId = Guid.NewGuid();

        var primarySnapshot = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId).WithStrategyName("baseline").Build();
        var secondarySnapshot = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId).WithStrategyName("low-media").Build();

        var primaryPath = (await factory.ForStrategy(Def("baseline", true))
            .WriteAsync(primarySnapshot, [], CancellationToken.None)).Path;
        var secondaryPath = (await factory.ForStrategy(Def("low-media", false))
            .WriteAsync(secondarySnapshot, [], CancellationToken.None)).Path;

        // The primary path is byte-identical to the pre-spec-137 layout.
        Assert.Equal(
            Path.Combine(_tempDir, companyId.ToString(), primarySnapshot.Id + ".json"),
            primaryPath);
        Assert.Equal(
            Path.Combine(
                _tempDir,
                StrategyScopedScoreSnapshotFileStoreFactory.StrategiesSegment,
                "low-media",
                companyId.ToString(),
                secondarySnapshot.Id + ".json"),
            secondaryPath);

        Assert.True(File.Exists(primaryPath));
        Assert.True(File.Exists(secondaryPath));
    }

    [Fact]
    public async Task NonPrimarySnapshots_AreInvisibleToThePrimaryReads()
    {
        // The efficacy/report reads go through the primary store; a non-primary strategy's snapshots must
        // never appear in them, or the reported series would silently mix strategies.
        var (factory, primary) = Create();
        var companyId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        await factory.ForStrategy(Def("low-media", false)).WriteAsync(
            new ScoreSnapshotBuilder()
                .WithCompanyId(companyId).WithCreatedAtUtc(createdAt).WithStrategyName("low-media").Build(),
            [],
            CancellationToken.None);

        Assert.Empty(await primary.ReadAllForCompanyAsync(companyId, CancellationToken.None));
        Assert.Null(await primary.ReadLatestBeforeAsync(
            companyId, createdAt.AddDays(1), CancellationToken.None));

        // ...and the non-primary store sees exactly its own.
        var isolated = await factory.ForStrategy(Def("low-media", false))
            .ReadAllForCompanyAsync(companyId, CancellationToken.None);
        Assert.Equal("low-media", Assert.Single(isolated).StrategyName);
    }
}
