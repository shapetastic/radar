using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.NewsTyping;

namespace Radar.Infrastructure.Tests.NewsTyping;

/// <summary>
/// Spec 181 §4: family checkpoints are append-only by construction — each checkpoint is its own timestamped
/// file, and a later checkpoint leaves the prior snapshot's bytes untouched.
/// </summary>
public sealed class FileFactFamilySnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-factfamily-tests-" + Guid.NewGuid().ToString("N"));

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

    private FileFactFamilySnapshotStore NewStore() => new(
        new FileFactFamilySnapshotStoreOptions { RootDirectory = _root },
        NullLogger<FileFactFamilySnapshotStore>.Instance);

    private static FactFamilySnapshot Snapshot(DateTimeOffset checkpointUtc, int memberCount)
    {
        var companyId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        var members = Enumerable.Range(1, memberCount)
            .Select(n => new Guid($"bbbbbbbb-0000-0000-0000-{n:D12}"))
            .ToList();
        return new FactFamilySnapshot(
            SchemaVersion: FactFamilySnapshot.CurrentSchemaVersion,
            BuilderIdentity: FactFamilyBuilder.IdentityString,
            CohortKey: "openai:test-model|news-typing-prompt-v1|news-typing-schema-v1|news-event-taxonomy-v1",
            CheckpointUtc: checkpointUtc,
            WindowStartUtc: checkpointUtc.AddDays(-30),
            WindowEndUtc: checkpointUtc,
            Families:
            [
                new FactFamilyRecord(
                    FamilyId: FactFamilyBuilder.FamilyIdFor(
                        companyId,
                        NewsObservationCaptureMode.ProspectiveRss,
                        DateOnly.FromDateTime(checkpointUtc.AddDays(-2).UtcDateTime),
                        [NewsEventType.RegulatoryOrLegal],
                        "faces legal scrutiny"),
                    CompanyId: companyId,
                    CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
                    RepresentativeFactId: members[0],
                    RepresentativeStatement: "Faces legal scrutiny",
                    CanonicalClaimKey: "faces legal scrutiny",
                    EventTypes: [NewsEventType.RegulatoryOrLegal],
                    MemberFactIds: members,
                    MemberCount: members.Count,
                    DistinctPublisherCount: members.Count,
                    EarliestObservedAtUtc: checkpointUtc.AddDays(-2)),
            ],
            FactsConsidered: memberCount,
            FactsWithoutCompany: 0);
    }

    [Fact]
    public async Task ALaterCheckpoint_WritesANewFile_LeavingThePriorSnapshotByteUnchanged()
    {
        var store = NewStore();
        var t1 = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddDays(1);

        Assert.True(await store.WriteAsync("openai-test-model", Snapshot(t1, 2), CancellationToken.None));
        var dir = Path.Combine(_root, "families", "openai-test-model");
        var firstPath = Assert.Single(Directory.EnumerateFiles(dir, "*.json"));
        Assert.EndsWith("20260823T120000Z.json", firstPath);
        var firstBytes = await File.ReadAllBytesAsync(firstPath);

        // The next checkpoint (a later-arriving member grew the family) is a NEW file; the old one is
        // untouched at the byte level.
        Assert.True(await store.WriteAsync("openai-test-model", Snapshot(t2, 3), CancellationToken.None));

        Assert.Equal(2, Directory.EnumerateFiles(dir, "*.json").Count());
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));
    }
}
