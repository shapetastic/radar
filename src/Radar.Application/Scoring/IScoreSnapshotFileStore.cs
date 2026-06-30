namespace Radar.Application.Scoring;

using Radar.Domain.Scoring;

/// <summary>
/// On-disk mirror of a company score snapshot and the evidence links that trace it back to the
/// contributing signals/evidence. Writes one JSON file per snapshot, grouped by company. A snapshot is
/// upsert-by-Id (AD-1): an existing file for the same snapshot id is overwritten (last-write-wins).
/// Returns the written path.
/// </summary>
public interface IScoreSnapshotFileStore
{
    Task<string> WriteAsync(
        CompanyScoreSnapshot snapshot,
        IReadOnlyList<ScoreEvidenceLink> links,
        CancellationToken ct);
}
