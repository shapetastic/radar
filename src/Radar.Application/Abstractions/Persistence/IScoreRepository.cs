using Radar.Domain.Scoring;

namespace Radar.Application.Abstractions.Persistence;

public interface IScoreRepository
{
    /// <remarks>
    /// Upsert by Id (last-write-wins). The relational implementation must preserve these
    /// semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddSnapshotAsync(CompanyScoreSnapshot snapshot, CancellationToken ct);

    /// <remarks>
    /// Upsert by Id (last-write-wins). The relational implementation must preserve these
    /// semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddEvidenceLinkAsync(ScoreEvidenceLink link, CancellationToken ct);
    Task<IReadOnlyList<CompanyScoreSnapshot>> GetSnapshotsForCompanyAsync(
        Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<ScoreEvidenceLink>> GetLinksForSnapshotAsync(
        Guid snapshotId, CancellationToken ct);
}
