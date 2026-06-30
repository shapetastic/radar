namespace Radar.Application.Scoring;

using Radar.Domain.Scoring;

/// <summary>
/// On-disk mirror of a company score snapshot and the evidence links that trace it back to the
/// contributing signals/evidence. Writes one JSON file per snapshot, grouped by company. A snapshot is
/// upsert-by-Id (AD-1): an existing file for the same snapshot id is overwritten (last-write-wins).
/// Returns the written path.
/// </summary>
/// <remarks>
/// Provenance invariant: every provided link must belong to the provided snapshot
/// (<c>link.ScoreSnapshotId == snapshot.Id</c>). Implementations throw <see cref="ArgumentException"/>
/// on a mismatch rather than persist an internally inconsistent file that would break the
/// score→signal/evidence trace.
/// </remarks>
public interface IScoreSnapshotFileStore
{
    Task<string> WriteAsync(
        CompanyScoreSnapshot snapshot,
        IReadOnlyList<ScoreEvidenceLink> links,
        CancellationToken ct);
}
