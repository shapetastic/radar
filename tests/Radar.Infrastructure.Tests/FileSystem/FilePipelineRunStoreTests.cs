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
