using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.NewsRisk;

/// <summary>
/// Spec 179 §4/§9: the archive's read seams — batch manifests resolved by EXPLICIT batch id (never a
/// nearest-time join) and the create-once prospective boundary — both failing closed (<c>null</c>) when
/// nothing is on disk.
/// </summary>
public sealed class NewsObservationArchiveReadSeamTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-archive-readseam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private FileNewsObservationArchive NewArchive() => new(
        new NewsObservationArchiveOptions { RootDirectory = _root },
        NullLogger<FileNewsObservationArchive>.Instance);

    private static NewsObservationBatch Batch(Guid batchId, bool fullUniverse = true) => new(
        BatchId: batchId,
        RunAsOfUtc: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        FullUniverse: fullUniverse,
        ObservationsAttempted: 1,
        ObservationsWritten: 1,
        ObservationsCrossRunDeduped: 0,
        ObservationsFailed: 0,
        CaptureProven: true,
        Collectors: []);

    [Fact]
    public async Task GetBatch_ResolvesByExplicitId_AcrossAFreshInstance()
    {
        var batchId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var archive = NewArchive();
        Assert.True(await archive.WriteBatchAsync(Batch(batchId), CancellationToken.None));
        Assert.True(await archive.WriteBatchAsync(
            Batch(otherId) with
            {
                RunAsOfUtc = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            },
            CancellationToken.None));

        var read = await NewArchive().GetBatchAsync(batchId, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(batchId, read!.BatchId);
        Assert.True(read.CaptureProven);
    }

    [Fact]
    public async Task GetBatch_UnknownId_ReturnsNull_FailClosed()
    {
        Assert.Null(await NewArchive().GetBatchAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Boundary_ReadsTheEstablishedCreateOnceFile_AndNullBeforeIt()
    {
        var archive = NewArchive();
        Assert.Null(await archive.ReadBoundaryAsync(CancellationToken.None));

        var batchId = Guid.NewGuid();
        Assert.True(await archive.WriteBatchAsync(Batch(batchId), CancellationToken.None));

        var boundary = await NewArchive().ReadBoundaryAsync(CancellationToken.None);
        Assert.NotNull(boundary);
        Assert.Equal(batchId, boundary!.EstablishedByBatchId);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero), boundary.FirstProspectiveCaptureAsOfUtc);
    }
}
