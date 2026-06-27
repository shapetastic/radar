using Radar.Domain.Scoring;

namespace Radar.Application.Abstractions.Persistence;

public interface IScoreRepository
{
    Task AddSnapshotAsync(CompanyScoreSnapshot snapshot, CancellationToken ct);
    Task AddEvidenceLinkAsync(ScoreEvidenceLink link, CancellationToken ct);
    Task<IReadOnlyList<CompanyScoreSnapshot>> GetSnapshotsForCompanyAsync(
        Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<ScoreEvidenceLink>> GetLinksForSnapshotAsync(
        Guid snapshotId, CancellationToken ct);
}
