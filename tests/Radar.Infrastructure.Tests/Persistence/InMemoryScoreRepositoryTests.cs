using Radar.Domain.Scoring;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Infrastructure.Tests.Persistence;

public class InMemoryScoreRepositoryTests
{
    private static CompanyScoreSnapshot MakeSnapshot(
        Guid id,
        Guid companyId,
        DateTimeOffset createdAtUtc)
        => new(
            Id: id,
            CompanyId: companyId,
            ScoringVersion: "v1",
            TrajectoryScore: 50,
            OpportunityScore: 40,
            AttentionScore: 30,
            EvidenceConfidenceScore: 60,
            SignalVelocityScore: 20,
            Explanation: "explanation",
            ComponentJson: "{}",
            WindowStartUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero),
            CreatedAtUtc: createdAtUtc);

    private static ScoreEvidenceLink MakeLink(Guid id, Guid snapshotId)
        => new(
            Id: id,
            ScoreSnapshotId: snapshotId,
            SignalId: Guid.NewGuid(),
            EvidenceId: Guid.NewGuid(),
            ContributionReason: "supports trajectory",
            ContributionWeight: 1);

    [Fact]
    public async Task GetSnapshotsForCompanyAsync_ReturnsOrderedByCreatedAtThenId()
    {
        var repo = new InMemoryScoreRepository();
        var companyId = Guid.NewGuid();

        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var first = MakeSnapshot(Guid.NewGuid(), companyId, t1);
        var second = MakeSnapshot(Guid.NewGuid(), companyId, t2);
        var third = MakeSnapshot(Guid.NewGuid(), companyId, t3);

        await repo.AddSnapshotAsync(third, CancellationToken.None);
        await repo.AddSnapshotAsync(first, CancellationToken.None);
        await repo.AddSnapshotAsync(second, CancellationToken.None);

        var result = await repo.GetSnapshotsForCompanyAsync(companyId, CancellationToken.None);

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task GetSnapshotsForCompanyAsync_EqualCreatedAt_BreaksTieById()
    {
        var repo = new InMemoryScoreRepository();
        var companyId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expected = new[] { idA, idB }.OrderBy(x => x).ToArray();

        await repo.AddSnapshotAsync(MakeSnapshot(idB, companyId, ts), CancellationToken.None);
        await repo.AddSnapshotAsync(MakeSnapshot(idA, companyId, ts), CancellationToken.None);

        var result = await repo.GetSnapshotsForCompanyAsync(companyId, CancellationToken.None);

        Assert.Equal(expected, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task GetLinksForSnapshotAsync_ReturnsOrderedById()
    {
        var repo = new InMemoryScoreRepository();
        var snapshotId = Guid.NewGuid();

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var expected = new[] { idA, idB, idC }.OrderBy(x => x).ToArray();

        await repo.AddEvidenceLinkAsync(MakeLink(idC, snapshotId), CancellationToken.None);
        await repo.AddEvidenceLinkAsync(MakeLink(idA, snapshotId), CancellationToken.None);
        await repo.AddEvidenceLinkAsync(MakeLink(idB, snapshotId), CancellationToken.None);

        var result = await repo.GetLinksForSnapshotAsync(snapshotId, CancellationToken.None);

        Assert.Equal(expected, result.Select(l => l.Id).ToArray());
    }
}
