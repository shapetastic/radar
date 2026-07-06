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
    public async Task WriteAsync_LinkBelongsToDifferentSnapshot_ThrowsAndWritesNothing()
    {
        var snapshot = new ScoreSnapshotBuilder().WithWindow(WindowStart, WindowEnd).Build();
        var otherSnapshot = new ScoreSnapshotBuilder().WithWindow(WindowStart, WindowEnd).Build();
        // Link targets a different snapshot id — persisting it would break the score→signal/evidence trace.
        var mismatchedLink = LinkFor(otherSnapshot, Guid.NewGuid(), Guid.NewGuid());
        var links = new List<ScoreEvidenceLink> { mismatchedLink };

        var store = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(snapshot, links, CancellationToken.None));
        Assert.Equal("links", ex.ParamName);

        // Nothing was written for either snapshot.
        Assert.False(File.Exists(
            Path.Combine(_tempDir, snapshot.CompanyId.ToString(), snapshot.Id + ".json")));
        Assert.False(File.Exists(
            Path.Combine(_tempDir, otherSnapshot.CompanyId.ToString(), otherSnapshot.Id + ".json")));
    }

    [Fact]
    public async Task ReadLatestBeforeAsync_ReturnsLatestSnapshotStrictlyBeforeInstant()
    {
        var companyId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);

        var first = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(40)
            .WithTrajectoryScore(45)
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();
        var second = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(70)
            .WithTrajectoryScore(66)
            .WithCreatedAtUtc(t2)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        await store.WriteAsync(first, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);
        await store.WriteAsync(second, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        // A cutoff after T2 returns the T2 snapshot (the latest).
        var afterT2 = t2.AddDays(1);
        var latest = await store.ReadLatestBeforeAsync(companyId, afterT2, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(second.Id, latest!.Id);
        Assert.Equal(70, latest.OpportunityScore);
        Assert.Equal(66, latest.TrajectoryScore);

        // Strictly-before: a cutoff exactly at T2 excludes T2 and returns the T1 snapshot.
        var beforeT2 = await store.ReadLatestBeforeAsync(companyId, t2, CancellationToken.None);
        Assert.NotNull(beforeT2);
        Assert.Equal(first.Id, beforeT2!.Id);
        Assert.Equal(40, beforeT2.OpportunityScore);
        Assert.Equal(45, beforeT2.TrajectoryScore);
    }

    [Fact]
    public async Task ReadLatestBeforeAsync_NoQualifyingSnapshotOrUnknownCompany_ReturnsNull()
    {
        var companyId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var snapshot = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId)
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        await store.WriteAsync(snapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        // Cutoff at (not before) the earliest snapshot → none qualify.
        Assert.Null(await store.ReadLatestBeforeAsync(companyId, t1, CancellationToken.None));

        // Unknown company (no directory) → null.
        Assert.Null(await store.ReadLatestBeforeAsync(
            Guid.NewGuid(), t1.AddYears(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReadLatestBeforeAsync_SkipsMalformedFileAndReturnsValidSnapshot()
    {
        var companyId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var snapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithOpportunityScore(55)
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        await store.WriteAsync(snapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        // Drop a garbage JSON file into the same company directory.
        var badFile = Path.Combine(_tempDir, companyId.ToString(), "bad.json");
        await File.WriteAllTextAsync(badFile, "{ this is not valid json");

        var latest = await store.ReadLatestBeforeAsync(
            companyId, t1.AddDays(1), CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(snapshot.Id, latest!.Id);
        Assert.Equal(55, latest.OpportunityScore);
    }

    [Fact]
    public async Task ReadLatestBeforeAsync_AlreadyCancelledToken_Throws()
    {
        var companyId = Guid.NewGuid();
        var snapshot = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        // At least one file must exist so the loop body runs ct.ThrowIfCancellationRequested().
        await store.WriteAsync(snapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadLatestBeforeAsync(companyId, DateTimeOffset.MaxValue, cancelled));
    }

    [Fact]
    public async Task WriteAsync_StampedSnapshot_SerializesAndRoundTripsScoringConfigVersion()
    {
        var companyId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithScoringConfigVersion("radar-scoring-config-v1")
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        var path = await store.WriteAsync(snapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        // The serialized JSON carries the camelCase scoringConfigVersion property.
        await using (var stream = File.OpenRead(path))
        {
            using var doc = await JsonDocument.ParseAsync(stream);
            Assert.Equal(
                "radar-scoring-config-v1",
                doc.RootElement.GetProperty("scoringConfigVersion").GetString());
        }

        // And it round-trips through ReadLatestBeforeAsync.
        var latest = await store.ReadLatestBeforeAsync(companyId, t1.AddDays(1), CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal("radar-scoring-config-v1", latest!.ScoringConfigVersion);
    }

    [Fact]
    public async Task ReadLatestBeforeAsync_OldFileMissingScoringConfigVersion_ReadsBackAsNull()
    {
        // An old on-disk snapshot written before the ScoringConfigVersion field existed lacks the
        // property entirely. Default System.Text.Json tolerates the missing member, so it reads back as
        // null (treated as not comparable) with no crash.
        var companyId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var companyDir = Path.Combine(_tempDir, companyId.ToString());
        Directory.CreateDirectory(companyDir);

        // Hand-written JSON matching the camelCase ScoreSnapshotFile shape but WITHOUT scoringConfigVersion.
        var json = $$"""
        {
          "snapshotId": "{{snapshotId}}",
          "companyId": "{{companyId}}",
          "scoringVersion": "mvp-engine-v1+radar-formula-v2",
          "trajectoryScore": 55,
          "opportunityScore": 60,
          "attentionScore": 50,
          "evidenceConfidenceScore": 70,
          "signalVelocityScore": 40,
          "explanation": "legacy snapshot",
          "componentJson": "{}",
          "windowStartUtc": "2026-01-08T00:00:00+00:00",
          "windowEndUtc": "2026-02-07T00:00:00+00:00",
          "createdAtUtc": "2026-02-01T00:00:00+00:00",
          "links": []
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(companyDir, snapshotId + ".json"), json);

        var store = CreateStore();
        var latest = await store.ReadLatestBeforeAsync(
            companyId, createdAt.AddDays(1), CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(snapshotId, latest!.Id);
        Assert.Null(latest.ScoringConfigVersion);
    }

    [Fact]
    public async Task ReadAllForCompanyAsync_ReturnsAllSnapshotsAscendingByCreatedThenId()
    {
        var companyId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);

        // Two snapshots share the SAME CreatedAtUtc so the Id tie-break is exercised.
        var idA = new Guid("00000000-0000-0000-0000-0000000000aa");
        var idB = new Guid("00000000-0000-0000-0000-0000000000bb");

        var later = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithCreatedAtUtc(t2)
            .WithWindow(WindowStart, WindowEnd)
            .Build();
        var tieB = new ScoreSnapshotBuilder()
            .WithId(idB)
            .WithCompanyId(companyId)
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();
        var tieA = new ScoreSnapshotBuilder()
            .WithId(idA)
            .WithCompanyId(companyId)
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        // Write out of order to prove the read sorts, not the disk order.
        await store.WriteAsync(later, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);
        await store.WriteAsync(tieB, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);
        await store.WriteAsync(tieA, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        var all = await store.ReadAllForCompanyAsync(companyId, CancellationToken.None);

        // Ascending by CreatedAtUtc (t1 before t2), tie-broken by Id (idA before idB).
        Assert.Equal([idA, idB, later.Id], all.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task ReadAllForCompanyAsync_SkipsForeignCompanyIdAndMalformedFiles()
    {
        var companyId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var t1 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var mine = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(companyId)
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        await store.WriteAsync(mine, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        // A foreign-CompanyId snapshot physically filed under this company's directory must be skipped.
        var foreignSnapshot = new ScoreSnapshotBuilder()
            .WithId(Guid.NewGuid())
            .WithCompanyId(foreignId)
            .WithCreatedAtUtc(t1)
            .WithWindow(WindowStart, WindowEnd)
            .Build();
        // FileScoreSnapshotStore files by the snapshot's own CompanyId, so write it then move it under companyId.
        var foreignPath = await store.WriteAsync(
            foreignSnapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);
        var misfiledPath = Path.Combine(_tempDir, companyId.ToString(), foreignSnapshot.Id + ".json");
        File.Move(foreignPath, misfiledPath);

        // Plus a garbage file.
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, companyId.ToString(), "bad.json"), "{ not valid json");

        var all = await store.ReadAllForCompanyAsync(companyId, CancellationToken.None);

        var only = Assert.Single(all);
        Assert.Equal(mine.Id, only.Id);
    }

    [Fact]
    public async Task ReadAllForCompanyAsync_MissingDirectory_ReturnsEmpty()
    {
        var store = CreateStore();

        var all = await store.ReadAllForCompanyAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(all);
    }

    [Fact]
    public async Task ReadAllForCompanyAsync_AlreadyCancelledToken_Throws()
    {
        var companyId = Guid.NewGuid();
        var snapshot = new ScoreSnapshotBuilder()
            .WithCompanyId(companyId)
            .WithWindow(WindowStart, WindowEnd)
            .Build();

        var store = CreateStore();
        // At least one file must exist so the loop body runs ct.ThrowIfCancellationRequested().
        await store.WriteAsync(snapshot, Array.Empty<ScoreEvidenceLink>(), CancellationToken.None);

        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadAllForCompanyAsync(companyId, cancelled));
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
