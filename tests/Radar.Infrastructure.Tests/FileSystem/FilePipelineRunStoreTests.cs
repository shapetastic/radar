using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Pipeline;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FilePipelineRunStoreTests : IDisposable
{
    private static readonly DateTimeOffset BaseInstant = new(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FilePipelineRunStoreTests()
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

    private FilePipelineRunStore CreateStore(string? rootDirectory = null) =>
        new(
            new FilePipelineRunStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FilePipelineRunStore>.Instance);

    private static PipelineRunRecord RecordAt(
        DateTimeOffset createdAtUtc,
        Guid? id = null,
        IReadOnlyList<string>? collectors = null) =>
        new(
            Id: id ?? Guid.NewGuid(),
            CreatedAtUtc: createdAtUtc,
            Collectors: collectors ?? ["sec-edgar", "RssPressReleaseCollector"],
            EvidenceCollected: 12,
            EvidenceNew: 5,
            SignalsExtracted: 7,
            SignalsValid: 6,
            SignalsApproved: 4,
            SignalsNeedingReview: 2,
            CompaniesScored: 9,
            SourcesChecked: 3,
            SourcesFailed: 1,
            ReportId: Guid.NewGuid());

    [Fact]
    public async Task WriteAsync_ThenReadRecent_RoundTripsAllFields()
    {
        var id = Guid.NewGuid();
        var record = RecordAt(BaseInstant, id);

        var store = CreateStore();
        var path = await store.WriteAsync(record, CancellationToken.None);

        // The file is written under {root}/{yyyy}/{MM}/run-...json.
        var expectedDir = Path.Combine(_tempDir, "2026", "02");
        Assert.StartsWith(expectedDir, path, StringComparison.Ordinal);
        Assert.True(File.Exists(path), $"Expected file at {path}.");

        var read = await store.ReadRecentAsync(10, CancellationToken.None);
        var roundTripped = Assert.Single(read);

        Assert.Equal(record.Id, roundTripped.Id);
        Assert.Equal(record.CreatedAtUtc, roundTripped.CreatedAtUtc);
        Assert.Equal(record.Collectors, roundTripped.Collectors);
        Assert.Equal(record.EvidenceCollected, roundTripped.EvidenceCollected);
        Assert.Equal(record.EvidenceNew, roundTripped.EvidenceNew);
        Assert.Equal(record.SignalsExtracted, roundTripped.SignalsExtracted);
        Assert.Equal(record.SignalsValid, roundTripped.SignalsValid);
        Assert.Equal(record.SignalsApproved, roundTripped.SignalsApproved);
        Assert.Equal(record.SignalsNeedingReview, roundTripped.SignalsNeedingReview);
        Assert.Equal(record.CompaniesScored, roundTripped.CompaniesScored);
        Assert.Equal(record.SourcesChecked, roundTripped.SourcesChecked);
        Assert.Equal(record.SourcesFailed, roundTripped.SourcesFailed);
        Assert.Equal(record.ReportId, roundTripped.ReportId);
    }

    [Fact]
    public async Task WriteAsync_ThenReadRecent_RoundTripsCollectionWarnings()
    {
        var warning = new CollectionHealthWarning(
            Code: "feeds-lost-before-collection",
            Severity: CollectionHealthSeverity.Warning,
            FeedType: "sec",
            DeclaredInSeed: 7,
            ReachedCollectors: 0,
            Message: "Seed declares 7 'sec' feed(s) but only 0 reached the collectors.");
        var record = RecordAt(BaseInstant) with { CollectionWarnings = [warning] };

        var store = CreateStore();
        await store.WriteAsync(record, CancellationToken.None);

        var read = await store.ReadRecentAsync(10, CancellationToken.None);
        var roundTripped = Assert.Single(read);

        Assert.NotNull(roundTripped.CollectionWarnings);
        var surfaced = Assert.Single(roundTripped.CollectionWarnings!);
        Assert.Equal(warning, surfaced);
    }

    [Fact]
    public async Task ReadRecentAsync_JsonWithoutCollectionWarnings_DeserializesToNull()
    {
        // Back-compat: an on-disk run file written before spec 98 has no collectionWarnings field. It must
        // still deserialize, with CollectionWarnings == null (the trailing optional default).
        const string legacyJson = """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "createdAtUtc": "2026-02-08T12:00:00+00:00",
              "collectors": ["sec-edgar"],
              "evidenceCollected": 12,
              "evidenceNew": 5,
              "signalsExtracted": 7,
              "signalsValid": 6,
              "signalsApproved": 4,
              "signalsNeedingReview": 2,
              "companiesScored": 9,
              "sourcesChecked": 3,
              "sourcesFailed": 1,
              "reportId": null
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "legacy-run.json"), legacyJson);

        var store = CreateStore();
        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        var record = Assert.Single(read);
        Assert.Null(record.CollectionWarnings);
    }

    [Fact]
    public async Task ReadRecentAsync_ReturnsNewestFirst_LimitedToCount()
    {
        var oldest = RecordAt(BaseInstant);
        var middle = RecordAt(BaseInstant.AddMinutes(1));
        var newest = RecordAt(BaseInstant.AddMinutes(2));

        var store = CreateStore();
        await store.WriteAsync(oldest, CancellationToken.None);
        await store.WriteAsync(middle, CancellationToken.None);
        await store.WriteAsync(newest, CancellationToken.None);

        var read = await store.ReadRecentAsync(2, CancellationToken.None);

        Assert.Equal(2, read.Count);
        Assert.Equal(newest.Id, read[0].Id);
        Assert.Equal(middle.Id, read[1].Id);
    }

    [Fact]
    public async Task ReadRecentAsync_MalformedNewestFile_StillReturnsCountValidRecords()
    {
        // The read walks newest-first and stops once it has `count` valid records. A malformed file in
        // the newest position must be skipped without causing the read to under-return older valid runs.
        var oldest = RecordAt(BaseInstant);
        var middle = RecordAt(BaseInstant.AddMinutes(1));
        var newest = RecordAt(BaseInstant.AddMinutes(2));

        var store = CreateStore();
        await store.WriteAsync(oldest, CancellationToken.None);
        await store.WriteAsync(middle, CancellationToken.None);
        var newestPath = await store.WriteAsync(newest, CancellationToken.None);

        // Corrupt the newest run file in place; it must be skipped, and the read must fall through to the
        // next-newest valid records rather than returning fewer than `count`.
        await File.WriteAllTextAsync(newestPath, "{ not valid");

        var read = await store.ReadRecentAsync(2, CancellationToken.None);

        Assert.Equal(2, read.Count);
        Assert.Equal(middle.Id, read[0].Id);
        Assert.Equal(oldest.Id, read[1].Id);
    }

    [Fact]
    public async Task ReadRecentAsync_WithZeroOrNegativeCount_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.WriteAsync(RecordAt(BaseInstant), CancellationToken.None);

        Assert.Empty(await store.ReadRecentAsync(0, CancellationToken.None));
        Assert.Empty(await store.ReadRecentAsync(-1, CancellationToken.None));
    }

    [Fact]
    public async Task ReadRecentAsync_MissingDirectory_ReturnsEmpty()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");
        var store = CreateStore(missing);

        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        Assert.Empty(read);
    }

    [Fact]
    public async Task ReadRecentAsync_SkipsMalformedFile()
    {
        var good = RecordAt(BaseInstant);

        var store = CreateStore();
        await store.WriteAsync(good, CancellationToken.None);

        // Drop a malformed JSON file into the root; it must be skipped, not break the read.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bad.json"), "{ not valid");

        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        var roundTripped = Assert.Single(read);
        Assert.Equal(good.Id, roundTripped.Id);
    }

    [Fact]
    public async Task ReadRecentAsync_SkipsNullRecordFile()
    {
        var good = RecordAt(BaseInstant);

        var store = CreateStore();
        await store.WriteAsync(good, CancellationToken.None);

        // A file whose contents deserialize to null (the JSON literal `null`) is a malformed
        // entry; it must be skipped like other unreadable files, not silently returned as null.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "null.json"), "null");

        var read = await store.ReadRecentAsync(10, CancellationToken.None);

        var roundTripped = Assert.Single(read);
        Assert.Equal(good.Id, roundTripped.Id);
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var id = Guid.NewGuid();
        var record = RecordAt(BaseInstant, id);

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(record, CancellationToken.None);

        // The attempted path is returned (no throw); the in-memory result still carries the counts.
        var expectedPath = Path.Combine(
            rootAsFile,
            "2026",
            "02",
            $"run-{BaseInstant.UtcDateTime:yyyyMMddTHHmmssfffZ}-{id}.json");
        Assert.Equal(expectedPath, path);
    }
}
