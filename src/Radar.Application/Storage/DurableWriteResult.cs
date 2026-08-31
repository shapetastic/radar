namespace Radar.Application.Storage;

/// <summary>
/// How a durable file-store write actually ended (spec 193 §1). Modelled on — deliberately not a second
/// invention beside — <c>Radar.Application.News.NewsObservationWriteOutcome</c>, the archive's typed write
/// outcome: <see cref="Written"/> is 0, so the SUCCESS case is the enum's default value exactly as it is
/// there, and a failure is a NAMED outcome rather than a discarded <c>bool</c>.
/// <para>
/// The mirror stores produce only <see cref="Written"/> and <see cref="Failed"/>. The archive's extra members
/// (<c>CrossRunDeduped</c>, <c>Conflict</c>) are insert-only semantics that do not exist here: the signal and
/// score-snapshot stores are upsert-by-Id (last-write-wins), so a write either lands or it does not.
/// </para>
/// <para>
/// <see cref="AlreadyAvailable"/> (spec 202 §1, widened by spec 206 §3) is the content-addressed,
/// insert-if-new case: the immutable record already existed, so nothing was written this call but the
/// record IS durably on disk. Exactly TWO stores are insert-if-new and may return it — the scoring-config
/// store (<c>FileScoringConfigStore</c>, <c>{fingerprint}.json</c>) and the raw-evidence store
/// (<c>FileRawEvidenceStore</c>, whose hydrated index/content-hash-keyed files make a re-collected item a
/// durable dedupe); no upsert store may. It is distinguished from <see cref="Written"/> so a run log can
/// tell "wrote it now" from "found it there" without changing the answer to the caller's actual question —
/// <see cref="DurableWriteResult.Written"/> is <c>true</c> for both.
/// </para>
/// </summary>
public enum DurableWriteOutcome
{
    /// <summary>The content was durably written to the returned path.</summary>
    Written = 0,

    /// <summary>
    /// The write degraded gracefully (a disk failure the writer caught, logged and did not rethrow).
    /// Nothing reached the returned path. The in-process copy — the in-memory index/repository entry — still
    /// exists, so the CURRENT run completes on what it has, but the item is NOT in the accrued store and the
    /// next run's history read will not see it. It must never be reported as stored.
    /// </summary>
    Failed,

    /// <summary>
    /// Nothing was written THIS call because the same immutable record already existed: the record is
    /// durable (<see cref="DurableWriteResult.Written"/> reports <c>true</c>), it just was not produced by
    /// this write. Returned only by the two insert-if-new stores — scoring config (spec 202 §1) and raw
    /// evidence (spec 206 §3).
    /// </summary>
    AlreadyAvailable,
}

/// <summary>
/// The outcome of one durable mirror write: the path that was ATTEMPTED (unchanged from the string these
/// stores used to return) plus whether the content actually reached it.
/// </summary>
/// <param name="Path">
/// The full path the store targeted. Present for both outcomes — an attempted path is what makes a failure
/// diagnosable — so it is never evidence that the file exists; <see cref="Outcome"/> is.
/// </param>
/// <param name="Outcome">Whether the content was durably persisted.</param>
public sealed record DurableWriteResult(string Path, DurableWriteOutcome Outcome)
{
    /// <summary>
    /// True iff the content is durably on disk at <see cref="Path"/> — written by this call
    /// (<see cref="DurableWriteOutcome.Written"/>) or already there before it
    /// (<see cref="DurableWriteOutcome.AlreadyAvailable"/>). The durability precondition asks exactly this
    /// question, and both answer yes.
    /// </summary>
    public bool Written =>
        Outcome is DurableWriteOutcome.Written or DurableWriteOutcome.AlreadyAvailable;

    /// <summary>A successful write to <paramref name="path"/>.</summary>
    public static DurableWriteResult Succeeded(string path) => new(path, DurableWriteOutcome.Written);

    /// <summary>A gracefully-degraded write that never reached <paramref name="path"/>.</summary>
    public static DurableWriteResult NotPersisted(string path) => new(path, DurableWriteOutcome.Failed);

    /// <summary>
    /// A content-addressed record that already existed at <paramref name="path"/>: durable, not written by
    /// this call (spec 202 §1). Insert-if-new stores only (scoring config, raw evidence — spec 206 §3).
    /// </summary>
    public static DurableWriteResult AlreadyOnDisk(string path) =>
        new(path, DurableWriteOutcome.AlreadyAvailable);

    /// <summary>
    /// Maps the shared writer's <c>bool</c> onto the typed outcome at the ONE place Infrastructure converts
    /// it, so "false means not persisted" is stated once rather than at every store.
    /// </summary>
    public static DurableWriteResult From(string path, bool written) =>
        written ? Succeeded(path) : NotPersisted(path);
}
