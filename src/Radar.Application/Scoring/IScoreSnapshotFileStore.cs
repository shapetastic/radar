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
/// <para>
/// The store is now read+write (it was previously write-only). The read
/// (<see cref="ReadLatestBeforeAsync"/>) is a targeted <i>scalar</i> read of the persisted snapshot
/// scores — it deliberately does NOT rehydrate the snapshot's <see cref="ScoreEvidenceLink"/>s. It
/// exists solely so the weekly report can compare against the previous run's snapshot; the current
/// report's provenance chain (current snapshot + its links) still comes from the in-memory repo.
/// </para>
/// </remarks>
public interface IScoreSnapshotFileStore
{
    Task<string> WriteAsync(
        CompanyScoreSnapshot snapshot,
        IReadOnlyList<ScoreEvidenceLink> links,
        CancellationToken ct);

    /// <summary>
    /// Returns the most recently created persisted snapshot for <paramref name="companyId"/> whose
    /// CreatedAtUtc is strictly before <paramref name="beforeUtc"/>, or null when the company has no
    /// qualifying persisted snapshot. Enables cross-run "vs previous snapshot" comparisons that the
    /// in-memory score repository cannot serve (it holds only the current process's snapshots).
    /// Only the scalar snapshot fields are required by callers; the returned snapshot need not
    /// rehydrate its ScoreEvidenceLinks. A read/deserialization failure of one file is skipped, never
    /// thrown; cancellation propagates.
    /// </summary>
    Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
        Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct);
}
