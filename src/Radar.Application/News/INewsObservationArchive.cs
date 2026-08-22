namespace Radar.Application.News;

/// <summary>How the archive disposed of one observation write (spec 177 §4).</summary>
public enum NewsObservationWriteOutcome
{
    /// <summary>A new immutable observation file was written and indexed.</summary>
    Written = 0,

    /// <summary>
    /// An identical observation (same id, same payload hash) already exists — in ANY year/month partition,
    /// from any earlier run. Nothing was written; the original record and its earliest
    /// <c>FirstObservedAtUtc</c> survive.
    /// </summary>
    CrossRunDeduped,

    /// <summary>
    /// The id is already held by a record carrying a DIFFERENT payload hash. This is fail-closed corruption
    /// detection, never a dedupe: nothing was written, nothing was overwritten, and the batch must count it
    /// as a failure (unproven capture).
    /// </summary>
    Conflict,

    /// <summary>The write could not be durably persisted (disk failure). Logged as Warning; counts as unproven capture.</summary>
    Failed,
}

/// <summary>
/// The immutable point-in-time news observation archive (spec 177). Insert-only, id-indexed, partitioned by
/// each record's immutable <c>FirstObservedAtUtc</c>. It is NOT an evidence repository: nothing in the
/// evidence → signal → score path reads it, and it feeds no fingerprint. The file implementation lives in
/// Infrastructure; the collection orchestration and the migration see only this seam.
/// </summary>
public interface INewsObservationArchive
{
    /// <summary>
    /// Writes <paramref name="record"/> if its observation id is new to the WHOLE archive (the hydrated
    /// index is consulted before any path is derived, so identity dedupes across all date partitions).
    /// Never throws for a per-record failure; caller cancellation propagates.
    /// </summary>
    Task<NewsObservationWriteOutcome> WriteAsync(NewsObservationRecord record, CancellationToken ct);

    /// <summary>
    /// Writes one pass's batch manifest, and — when <paramref name="batch"/> is the first SUCCESSFUL
    /// full-universe prospective batch — establishes the create-once <c>boundary.json</c> carrying
    /// <c>firstProspectiveCaptureAsOfUtc</c> from that actual run. The boundary is never overwritten and is
    /// never established by a company-filtered pass. Returns whether the manifest was durably written
    /// (a false is a Warning-logged, unproven-capture outcome, never an abort).
    /// </summary>
    Task<bool> WriteBatchAsync(NewsObservationBatch batch, CancellationToken ct);

    /// <summary>
    /// Every archived observation, hydrated from disk, in deterministic
    /// (<c>FirstObservedAtUtc</c>, <c>ObservationId</c>) order (AD-3). Read by the migration's
    /// retrospective-fetch mode; the live pipeline never reads it.
    /// </summary>
    Task<IReadOnlyList<NewsObservationRecord>> GetAllAsync(CancellationToken ct);
}
