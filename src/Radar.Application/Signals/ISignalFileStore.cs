namespace Radar.Application.Signals;

using Radar.Domain.Signals;

/// <summary>
/// On-disk mirror of a reviewed signal and its review record. Writes one JSON file per signal under
/// the signals root, capturing provenance (evidence id, resolved company id) and the embedded review.
/// A signal is upsert-by-Id (AD-1): an existing file for the same signal id is overwritten
/// (last-write-wins). Returns the written path.
/// </summary>
/// <remarks>
/// This store is now <b>read+write</b> (it was write-only). <see cref="ReadApprovedInWindowAsync"/> adds
/// a targeted, activity-only window read that lets the scoring engine compare a company's current-window
/// signal activity against a PRIOR run's activity (which the in-memory signal repository cannot serve — it
/// holds only the current process's signals). The read is deliberately <b>provenance-free</b>: it does not
/// rehydrate evidence or <c>ScoreEvidenceLink</c>s — it is a scalar/activity read for velocity, not a
/// general repository rehydration. Per AD-6 the previous window carries no provenance by design.
/// </remarks>
public interface ISignalFileStore
{
    /// <summary>
    /// Mirrors the reviewed <paramref name="signal"/> and its <paramref name="review"/> to disk.
    /// The review must belong to the signal (<c>review.SignalId == signal.Id</c>), otherwise an
    /// <see cref="ArgumentException"/> is thrown to protect the review→signal provenance trace.
    /// </summary>
    Task<string> WriteAsync(Signal signal, SignalReview review, CancellationToken ct);

    /// <summary>
    /// Returns the persisted Approved signals for <paramref name="companyId"/> whose ObservedAtUtc is in
    /// (<paramref name="startExclusiveUtc"/>, <paramref name="endInclusiveUtc"/>] — the exclusive-start,
    /// inclusive-end window convention the scoring engine uses (AD-6). Enables the cross-run
    /// SignalVelocity previous-window comparison that the in-memory signal repository cannot serve (it holds
    /// only the current process's signals). These signals are consumed ACTIVITY-ONLY (Strength magnitude for
    /// velocity) — callers do NOT need evidence, so the returned signals need not rehydrate any provenance
    /// links; this is a targeted scalar read, not a general repository rehydration. A read/deserialization
    /// failure of one file is skipped, never thrown; cancellation propagates. Results are deterministically
    /// ordered (ObservedAtUtc, then Id — AD-3). The read returns at most ONE signal per stable identity
    /// <c>(CompanyId, EvidenceId, Type, Direction)</c>, collapsing cross-run duplicate persisted copies (the
    /// same signal re-minted with a fresh id each run) so this activity-only previous window is deterministic
    /// and not inflated by how many times the pipeline has run.
    /// </summary>
    Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
        Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct);
}
