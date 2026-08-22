using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

public sealed class FileNewsObservationArchiveTests : IDisposable
{
    private static readonly DateTimeOffset January =
        new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset March =
        new(2026, 3, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FileNewsObservationArchiveTests()
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

    private FileNewsObservationArchive CreateArchive() =>
        new(
            new NewsObservationArchiveOptions { RootDirectory = _tempDir },
            NullLogger<FileNewsObservationArchive>.Instance);

    private static NewsObservationCandidate Candidate(
        string url = "https://news.google.com/rss/articles/AAA",
        string headline = "Rocket Lab wins new launch contract - SpaceNews",
        string publisher = "SpaceNews",
        string? descriptionRaw = "<a>Rocket Lab wins new launch contract</a>",
        DateTimeOffset? retrievedAt = null) =>
        new(
            CompanyId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Ticker: "RKLB",
            Collector: "newssearch",
            QueryPhrase: "Rocket Lab",
            FeedId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            FeedName: "Rocket Lab — News",
            GoogleLandingUrl: url,
            Publisher: publisher,
            PublisherSiteUrl: "https://spacenews.com",
            Headline: headline,
            DescriptionRaw: descriptionRaw,
            DescriptionText: descriptionRaw is null ? null : "Rocket Lab wins new launch contract",
            DescriptionTruncated: false,
            PublishedAtUtc: (retrievedAt ?? January).AddHours(-2),
            RetrievedAtUtc: retrievedAt ?? January);

    private static NewsObservationBatch Batch(
        bool fullUniverse = true, int failed = 0, DateTimeOffset? asOf = null) =>
        new(
            BatchId: Guid.NewGuid(),
            RunAsOfUtc: asOf ?? January,
            SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
            FullUniverse: fullUniverse,
            ObservationsAttempted: 1,
            ObservationsWritten: 1 - failed,
            ObservationsCrossRunDeduped: 0,
            ObservationsFailed: failed,
            CaptureProven: failed == 0,
            Collectors: []);

    [Fact]
    public async Task WriteAsync_NewObservation_WritesPartitionedFileAndRoundTrips()
    {
        var archive = CreateArchive();
        var record = NewsObservationRecord.Prospective(Candidate());

        var outcome = await archive.WriteAsync(record, CancellationToken.None);

        Assert.Equal(NewsObservationWriteOutcome.Written, outcome);
        var expectedPath = Path.Combine(
            _tempDir, "observations", "2026", "01", record.ObservationId.ToString("D") + ".json");
        Assert.True(File.Exists(expectedPath));

        // Round-trip through a FRESH instance (hydration) is lossless.
        var hydrated = Assert.Single(await CreateArchive().GetAllAsync(CancellationToken.None));
        Assert.Equal(record, hydrated);
    }

    [Fact]
    public async Task WriteAsync_SamePayloadInALaterMonth_DedupesThroughTheIndex_NoSecondPartitionFile()
    {
        // Spec 177 §4: a path check alone is NOT a dedupe mechanism — the same id re-observed in a later
        // month derives a DIFFERENT partition path, so only the hydrated id index can collapse it.
        var archive = CreateArchive();
        var original = NewsObservationRecord.Prospective(Candidate(retrievedAt: January));
        Assert.Equal(
            NewsObservationWriteOutcome.Written, await archive.WriteAsync(original, CancellationToken.None));

        // A FRESH instance (forcing hydration from disk) sees the March re-observation of the identical
        // payload and resolves it to the original record.
        var later = NewsObservationRecord.Prospective(Candidate(retrievedAt: March));
        Assert.Equal(original.ObservationId, later.ObservationId); // identical payload ⇒ identical identity

        var freshArchive = CreateArchive();
        var outcome = await freshArchive.WriteAsync(later, CancellationToken.None);

        Assert.Equal(NewsObservationWriteOutcome.CrossRunDeduped, outcome);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "observations", "2026", "03")));

        // The earliest FirstObservedAtUtc survives.
        var kept = Assert.Single(await freshArchive.GetAllAsync(CancellationToken.None));
        Assert.Equal(January, kept.FirstObservedAtUtc);
    }

    [Fact]
    public async Task WriteAsync_ChangedProviderContent_IsALaterObservation_NotAnOverwrite()
    {
        var archive = CreateArchive();
        var first = NewsObservationRecord.Prospective(Candidate(retrievedAt: January));
        var changed = NewsObservationRecord.Prospective(Candidate(
            descriptionRaw: "<a>Rocket Lab wins new launch contract — UPDATED</a>", retrievedAt: March));

        Assert.NotEqual(first.PayloadHash, changed.PayloadHash);
        Assert.NotEqual(first.ObservationId, changed.ObservationId);

        Assert.Equal(NewsObservationWriteOutcome.Written, await archive.WriteAsync(first, CancellationToken.None));
        Assert.Equal(NewsObservationWriteOutcome.Written, await archive.WriteAsync(changed, CancellationToken.None));

        Assert.Equal(2, (await archive.GetAllAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentIdenticalWriters_NeverOverwrite_ExactlyOneFile()
    {
        var archive = CreateArchive();
        var record = NewsObservationRecord.Prospective(Candidate());

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => archive.WriteAsync(record, CancellationToken.None))));

        Assert.Equal(1, outcomes.Count(o => o == NewsObservationWriteOutcome.Written));
        Assert.Equal(7, outcomes.Count(o => o == NewsObservationWriteOutcome.CrossRunDeduped));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_tempDir, "observations"), "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WriteAsync_SameIdDifferentPayloadHash_FailsClosedAsConflict_NeverADedupe()
    {
        var archive = CreateArchive();
        var record = NewsObservationRecord.Prospective(Candidate());
        Assert.Equal(NewsObservationWriteOutcome.Written, await archive.WriteAsync(record, CancellationToken.None));

        // Impossible from honest writes (the id derives from the hash) — i.e. corruption detection.
        var conflicting = record with { PayloadHash = new string('0', 64) };
        var outcome = await archive.WriteAsync(conflicting, CancellationToken.None);

        Assert.Equal(NewsObservationWriteOutcome.Conflict, outcome);
        // The original stands untouched.
        Assert.Equal(record, Assert.Single(await archive.GetAllAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Hydration_LegacyDuplicateFiles_KeepOrdinalFirstIdenticalRecord_FilesRetained()
    {
        // Simulate the legacy shape the mechanism must tolerate: the SAME identical record persisted under
        // two partitions (a pre-index artifact). Hydration collapses the identity to the ordinal-first file
        // and retains both files on disk.
        var record = NewsObservationRecord.Prospective(Candidate(retrievedAt: January));
        var json = System.Text.Json.JsonSerializer.Serialize(
            record, Radar.Infrastructure.FileSystem.RadarFileStoreJson.Options);
        WriteRaw("observations/2026/01/" + record.ObservationId.ToString("D") + ".json", json);
        WriteRaw("observations/2026/03/" + record.ObservationId.ToString("D") + ".json", json);

        var archive = CreateArchive();
        Assert.Single(await archive.GetAllAsync(CancellationToken.None));
        Assert.Equal(2, Directory.EnumerateFiles(
            Path.Combine(_tempDir, "observations"), "*.json", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Hydration_SameIdDifferentPayloadFiles_SkipsTheConflictingLaterFile()
    {
        var record = NewsObservationRecord.Prospective(Candidate(retrievedAt: January));
        var conflicting = record with { PayloadHash = new string('f', 64) };
        WriteRaw(
            "observations/2026/01/" + record.ObservationId.ToString("D") + ".json",
            System.Text.Json.JsonSerializer.Serialize(record, Radar.Infrastructure.FileSystem.RadarFileStoreJson.Options));
        WriteRaw(
            "observations/2026/03/" + record.ObservationId.ToString("D") + ".json",
            System.Text.Json.JsonSerializer.Serialize(conflicting, Radar.Infrastructure.FileSystem.RadarFileStoreJson.Options));

        var archive = CreateArchive();
        var hydrated = Assert.Single(await archive.GetAllAsync(CancellationToken.None));

        // The ordinal-first (2026/01) record survives; the conflicting one is skipped, never merged.
        Assert.Equal(record.PayloadHash, hydrated.PayloadHash);
    }

    [Fact]
    public async Task WriteBatchAsync_WritesAsOfNamedManifest()
    {
        var archive = CreateArchive();
        var batch = Batch(asOf: new DateTimeOffset(2026, 1, 10, 9, 30, 15, TimeSpan.Zero));

        Assert.True(await archive.WriteBatchAsync(batch, CancellationToken.None));

        var path = Path.Combine(_tempDir, "batches", "20260110T093015Z.json");
        Assert.True(File.Exists(path));
        Assert.Contains(batch.BatchId.ToString(), File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteBatchAsync_SameAsOfToken_NeverOverwrites_FallsBackToBatchIdSuffixedName()
    {
        var archive = CreateArchive();
        var asOf = new DateTimeOffset(2026, 1, 10, 9, 30, 15, TimeSpan.Zero);
        var first = Batch(asOf: asOf);
        var second = Batch(asOf: asOf);

        Assert.True(await archive.WriteBatchAsync(first, CancellationToken.None));
        var tokenPath = Path.Combine(_tempDir, "batches", "20260110T093015Z.json");
        var firstBytes = File.ReadAllBytes(tokenPath);

        // A manifest is a run record: the taken token falls back to a suffixed name, and the first
        // manifest survives byte-untouched.
        Assert.True(await archive.WriteBatchAsync(second, CancellationToken.None));
        Assert.Equal(firstBytes, File.ReadAllBytes(tokenPath));
        var suffixedPath = Path.Combine(
            _tempDir, "batches", "20260110T093015Z-" + second.BatchId.ToString("N") + ".json");
        Assert.True(File.Exists(suffixedPath));
        Assert.Contains(second.BatchId.ToString(), File.ReadAllText(suffixedPath));
    }

    [Fact]
    public async Task Boundary_IsEstablishedOnce_ByTheFirstSuccessfulFullUniverseBatch_AndNeverOverwritten()
    {
        var archive = CreateArchive();
        var first = Batch(asOf: new DateTimeOffset(2026, 1, 10, 9, 0, 0, TimeSpan.Zero));
        var second = Batch(asOf: new DateTimeOffset(2026, 1, 11, 9, 0, 0, TimeSpan.Zero));

        Assert.True(await archive.WriteBatchAsync(first, CancellationToken.None));
        var boundaryPath = Path.Combine(_tempDir, "boundary.json");
        Assert.True(File.Exists(boundaryPath));
        var establishedBytes = File.ReadAllBytes(boundaryPath);
        Assert.Contains(first.BatchId.ToString(), File.ReadAllText(boundaryPath));

        // A later successful full-universe batch leaves the boundary byte-untouched.
        Assert.True(await archive.WriteBatchAsync(second, CancellationToken.None));
        Assert.Equal(establishedBytes, File.ReadAllBytes(boundaryPath));
    }

    [Fact]
    public async Task Boundary_IsNotEstablished_ByAFilteredPass_OrAnUnprovenBatch()
    {
        var archive = CreateArchive();

        // Spec 161 company-filtered pass: may capture observations, can NEVER establish the boundary.
        Assert.True(await archive.WriteBatchAsync(Batch(fullUniverse: false), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_tempDir, "boundary.json")));

        // An unproven batch (failed observation writes) cannot establish it either: "prospective capture
        // began here" must not point at a run that demonstrably lost observations.
        Assert.True(await archive.WriteBatchAsync(
            Batch(failed: 1, asOf: March), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_tempDir, "boundary.json")));

        // The next clean full-universe batch establishes it, with ITS OWN as-of.
        var clean = Batch(asOf: March.AddDays(1));
        Assert.True(await archive.WriteBatchAsync(clean, CancellationToken.None));
        Assert.Contains(clean.BatchId.ToString(), File.ReadAllText(Path.Combine(_tempDir, "boundary.json")));
    }

    [Fact]
    public async Task GetAllAsync_IsDeterministicallyOrdered()
    {
        var archive = CreateArchive();
        var later = NewsObservationRecord.Prospective(Candidate(url: "https://x/2", retrievedAt: March));
        var earlier = NewsObservationRecord.Prospective(Candidate(url: "https://x/1", retrievedAt: January));
        await archive.WriteAsync(later, CancellationToken.None);
        await archive.WriteAsync(earlier, CancellationToken.None);

        var all = await archive.GetAllAsync(CancellationToken.None);

        Assert.Equal([earlier.ObservationId, later.ObservationId], all.Select(o => o.ObservationId));
    }

    private void WriteRaw(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
