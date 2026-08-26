namespace Radar.Application.Scoring;

using Radar.Application.Storage;
using Radar.Domain.Scoring;

/// <summary>
/// On-disk mirror of a company score snapshot and the evidence links that trace it back to the
/// contributing signals/evidence. Writes one JSON file per snapshot, grouped by company. A snapshot is
/// upsert-by-Id (AD-1): an existing file for the same snapshot id is overwritten (last-write-wins).
/// Returns the attempted path AND whether the content actually reached it.
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
    /// <summary>
    /// Mirrors <paramref name="snapshot"/> and its <paramref name="links"/> to disk.
    /// <para>
    /// SPEC 193 §1: a disk failure still degrades gracefully (the run never crashes; the in-memory score
    /// repository copy still exists) — but the failure is now RETURNED as a
    /// <see cref="DurableWriteOutcome.Failed"/> result rather than discarded, so the pipeline can count it
    /// and stop reporting a snapshot that never reached disk as durably stored. The returned path is the
    /// ATTEMPTED path in both outcomes; <see cref="DurableWriteResult.Outcome"/> is the only proof.
    /// </para>
    /// </summary>
    Task<DurableWriteResult> WriteAsync(
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

    /// <summary>
    /// Returns ALL persisted snapshots for the company, ascending by CreatedAtUtc then Id (AD-3), scalar fields
    /// only (Links intentionally empty — same posture as <see cref="ReadLatestBeforeAsync"/>). The
    /// efficacy/validation layer's read seam over score history (AD-14 amendment); read-only, never writes. A
    /// malformed or foreign-CompanyId file is skipped + logged, never thrown; a missing directory returns an
    /// empty list; cancellation propagates.
    /// </summary>
    Task<IReadOnlyList<CompanyScoreSnapshot>> ReadAllForCompanyAsync(Guid companyId, CancellationToken ct);
}
