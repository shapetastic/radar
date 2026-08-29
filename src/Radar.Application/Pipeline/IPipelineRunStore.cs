using Radar.Application.Storage;

namespace Radar.Application.Pipeline;

/// <summary>
/// Persists and reads the append-only pipeline run log (AD-8). Each completed run writes one
/// <see cref="PipelineRunRecord"/>; the store is the durable history of when runs happened, which
/// collectors ran, and how the run's counts compared to prior runs. All file I/O stays behind this
/// interface in Infrastructure (AD-5); the Application layer never touches the disk directly.
/// </summary>
public interface IPipelineRunStore
{
    /// <summary>
    /// Persists <paramref name="record"/> to the run log. Best-effort: disk failures degrade gracefully (the
    /// record is not lost from the returned in-memory result) and never abort the run — but the outcome is
    /// REPORTED (spec 201 §1): the returned <see cref="DurableWriteResult"/> carries the attempted path plus
    /// whether the record actually reached it, and the caller must not read the path as proof of storage.
    /// </summary>
    Task<DurableWriteResult> WriteAsync(PipelineRunRecord record, CancellationToken ct);

    /// <summary>
    /// Returns up to <paramref name="count"/> most-recent run records, newest-first, ordered by
    /// <see cref="PipelineRunRecord.CreatedAtUtc"/> descending then <see cref="PipelineRunRecord.Id"/>
    /// descending (AD-3 determinism). A non-positive <paramref name="count"/> returns an empty list.
    /// </summary>
    Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct);

    /// <summary>
    /// Returns EVERY run record whose <see cref="PipelineRunRecord.CreatedAtUtc"/> falls in the INCLUSIVE
    /// range <c>[startInclusiveUtc, endInclusiveUtc]</c>, ordered by <c>CreatedAtUtc</c> ascending then
    /// <see cref="PipelineRunRecord.Id"/> ascending (AD-3 determinism). An inverted range returns an empty
    /// list. Malformed/unreadable files are skipped and logged, never thrown; cancellation propagates.
    /// <para>
    /// <b>Bounded by TIME, not by count, deliberately (spec 169).</b> The AD-16 coverage chain has to be able
    /// to tell "no run happened in this window" from "the run happened but I only asked for the newest N and
    /// it fell off the end". <see cref="ReadRecentAsync"/> cannot make that distinction, so mistaking its
    /// truncation for absence would silently drop company-dates that were in fact fully covered.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PipelineRunRecord>> ReadBetweenAsync(
        DateTimeOffset startInclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct);
}
