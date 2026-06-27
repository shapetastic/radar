using System.Collections.Concurrent;
using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Scoring;

namespace Radar.Infrastructure.Persistence.InMemory;

public sealed class InMemoryScoreRepository : IScoreRepository
{
    private readonly ConcurrentDictionary<Guid, CompanyScoreSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, ScoreEvidenceLink> _links = new();

    public Task AddSnapshotAsync(CompanyScoreSnapshot snapshot, CancellationToken ct)
    {
        _snapshots[snapshot.Id] = snapshot;
        return Task.CompletedTask;
    }

    public Task AddEvidenceLinkAsync(ScoreEvidenceLink link, CancellationToken ct)
    {
        _links[link.Id] = link;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CompanyScoreSnapshot>> GetSnapshotsForCompanyAsync(
        Guid companyId, CancellationToken ct)
    {
        IReadOnlyList<CompanyScoreSnapshot> result = _snapshots.Values
            .Where(s => s.CompanyId == companyId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ScoreEvidenceLink>> GetLinksForSnapshotAsync(
        Guid snapshotId, CancellationToken ct)
    {
        IReadOnlyList<ScoreEvidenceLink> result = _links.Values
            .Where(l => l.ScoreSnapshotId == snapshotId)
            .ToList();
        return Task.FromResult(result);
    }
}
