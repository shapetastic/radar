using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Scoring;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileScoreSnapshotStoreTests : IDisposable
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 2, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public FileScoreSnapshotStoreTests()
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

    private FileScoreSnapshotStore CreateStore(string? rootDirectory = null) =>
        new(
            new FileScoreSnapshotStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileScoreSnapshotStore>.Instance);

    private static ScoreEvidenceLink LinkFor(
        CompanyScoreSnapshot snapshot,
        Guid signalId,
        Guid evidenceId,
        int weight = 3,
        string reason = "Material customer win contributed to trajectory.") =>
        new(
            Id: Guid.NewGuid(),
            ScoreSnapshotId: snapshot.Id,
            SignalId: signalId,
            EvidenceId: evidenceId,
            ContributionReason: reason,
            ContributionWeight: weight);

    [Fact]
    public async Task WriteAsync_NewSnapshot_WritesFileAtExpectedPathAndRoundTrips()
    {
        var snapshot = new ScoreSnapshotBuilder()
            .WithScoringVersion("radar-formula-v1")
            .WithTrajectoryScore(72)
            .WithOpportunityScore(64)
            .WithAttentionScore(58)
            .WithEvidenceConfidenceScore(81)
            .WithSignalVelocityScore(40)
            .WithWindow(WindowStart, WindowEnd)
            .Build();
        var signalId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var links = new List<ScoreEvidenceLink> { LinkFor(snapshot, signalId, evidenceId, weight: 5) };

        var store = CreateStore();
        var path = await store.WriteAsync(snapshot, links, CancellationToken.None);

        var expectedPath = Path.Combine(
            _tempDir, snapshot.CompanyId.ToString(), snapshot.Id + ".json");
        Assert.Equal(expectedPath, path);
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");

        await using var stream = File.OpenRead(expectedPath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        Assert.Equal(snapshot.Id.ToString(), root.GetProperty("snapshotId").GetString());
        Assert.Equal(snapshot.CompanyId.ToString(), root.GetProperty("companyId").GetString());
        Assert.Equal("radar-formula-v1", root.GetProperty("scoringVersion").GetString());
        Assert.Equal(72, root.GetProperty("trajectoryScore").GetInt32());
        Assert.Equal(64, root.GetProperty("opportunityScore").GetInt32());
        Assert.Equal(58, root.GetProperty("attentionScore").GetInt32());
        Assert.Equal(81, root.GetProperty("evidenceConfidenceScore").GetInt32());
        Assert.Equal(40, root.GetProperty("signalVelocityScore").GetInt32());
        Assert.Equal(snapshot.Explanation, root.GetProperty("explanation").GetString());
        Assert.Equal(WindowStart, root.GetProperty("windowStartUtc").GetDateTimeOffset());
        Assert.Equal(WindowEnd, root.GetProperty("windowEndUtc").GetDateTimeOffset());

        // Links trace the score back to its contributing signal/evidence (provenance).
        var linksElement = root.GetProperty("links");
        Assert.Equal(JsonValueKind.Array, linksElement.ValueKind);
        var link = Assert.Single(linksElement.EnumerateArray());
        Assert.Equal(snapshot.Id.ToString(), link.GetProperty("scoreSnapshotId").GetString());
        Assert.Equal(signalId.ToString(), link.GetProperty("signalId").GetString());
        Assert.Equal(evidenceId.ToString(), link.GetProperty("evidenceId").GetString());
        Assert.Equal(5, link.GetProperty("contributionWeight").GetInt32());
    }

    [Fact]
    public async Task WriteAsync_ZeroContributionSnapshot_WritesValidFileWithEmptyLinks()
    {
        var snapshot = new ScoreSnapshotBuilder()
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        var path = await store.WriteAsync(
            snapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        Assert.True(File.Exists(path), $"Expected file at {path}.");

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        var linksElement = doc.RootElement.GetProperty("links");
        Assert.Equal(JsonValueKind.Array, linksElement.ValueKind);
        Assert.Empty(linksElement.EnumerateArray());
    }

    [Fact]
    public async Task WriteAsync_CalledTwiceForSameSnapshotId_IsOverwriteAllowedLastWriteWins()
    {
        var id = Guid.NewGuid();
        var first = new ScoreSnapshotBuilder()
            .WithId(id)
            .WithTrajectoryScore(30)
            .WithWindow(WindowStart, WindowEnd)
            .Build();
        var second = first with { TrajectoryScore = 90 };

        var store = CreateStore();
        await store.WriteAsync(first, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);
        await store.WriteAsync(second, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        // Exactly one file for the snapshot id — proves it is NOT insert-only.
        var dir = Path.Combine(_tempDir, first.CompanyId.ToString());
        var files = Directory.GetFiles(dir);
        Assert.Single(files);

        await using var stream = File.OpenRead(Path.Combine(dir, id + ".json"));
        using var doc = await JsonDocument.ParseAsync(stream);

        // Contents reflect the second write (last-write-wins).
        Assert.Equal(90, doc.RootElement.GetProperty("trajectoryScore").GetInt32());
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var snapshot = new ScoreSnapshotBuilder().WithWindow(WindowStart, WindowEnd).Build();

        var store = CreateStore(rootAsFile);

        var path = await store.WriteAsync(
            snapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        // The attempted path is returned (no throw); the in-memory copy still exists in production.
        var expectedPath = Path.Combine(
            rootAsFile, snapshot.CompanyId.ToString(), snapshot.Id + ".json");
        Assert.Equal(expectedPath, path);
    }
}
