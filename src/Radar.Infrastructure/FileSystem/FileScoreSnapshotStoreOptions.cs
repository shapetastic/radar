using Radar.Domain.Scoring;

namespace Radar.Infrastructure.FileSystem;

public sealed class FileScoreSnapshotStoreOptions
{
    public required string RootDirectory { get; init; }

    /// <summary>
    /// Optional deterministic file-name selector for a snapshot, used verbatim as the leaf name under
    /// <c>{RootDirectory}/{companyId}/</c>. <c>null</c> (the default) keeps the established
    /// <c>{snapshotId}.json</c> naming, so the forward path is byte-identical and untouched.
    /// <para>
    /// It exists for <b>replay</b> (spec 139). A replay's snapshot ids are freshly minted on every call, so
    /// id-named files would ACCUMULATE: re-running the same replay over an unchanged store would double the
    /// files on disk instead of reproducing them, and "two identical replays are diffable to zero" would be
    /// false at the file level even though every score was identical. Keying the file name on the as-of
    /// instant instead makes a re-run overwrite in place, which is what makes replay idempotent on disk.
    /// </para>
    /// <para>
    /// The selector must return a plain file name (no directory separators) and must be a pure function of
    /// the snapshot, or the store's determinism guarantee is lost. <see cref="FileScoreSnapshotStore"/>
    /// rejects a non-plain name rather than joining it into a path.
    /// </para>
    /// </summary>
    public Func<CompanyScoreSnapshot, string>? SnapshotFileName { get; init; }

    /// <summary>
    /// Optional observer invoked when a write has SUCCESSFULLY replaced a file that was already on disk at
    /// the same path (spec 148). <c>null</c> (the default) is the forward/live path: the existence probe is
    /// not even made, so nothing about the established behaviour changes.
    /// <para>
    /// The probe necessarily happens before the write (afterwards the file always exists) but the callback
    /// fires only on the success branch: serialization or the graceful disk-failure path can still abandon the
    /// write, and the aggregated operator warning this feeds must not assert a replacement that never
    /// happened.
    /// </para>
    /// <para>
    /// It exists because <see cref="SnapshotFileName"/> above makes replay idempotent by NAME — and the very
    /// same property means a second replay under the same label replaces a series that may already have been
    /// ranked, silently. The only place that can tell is <see cref="FileScoreSnapshotStore.WriteAsync"/>,
    /// which knows the target path before it writes; putting the probe anywhere else would need a second copy
    /// of the path arithmetic, which would then be free to drift.
    /// </para>
    /// <para>
    /// It must not throw and must not write: it is bookkeeping for an aggregated operator warning, never a
    /// gate. The write proceeds either way (upsert-by-Id / last-write-wins is unchanged).
    /// </para>
    /// </summary>
    public Action<CompanyScoreSnapshot>? OnSnapshotOverwritten { get; init; }

    /// <summary>
    /// How this store reports a graceful write failure (spec 195 §1). The default,
    /// <see cref="GracefulFileWriteFailureLogging.Immediate"/>, keeps the per-file Warning — the safe
    /// direction, so a NEW construction site gets its failures reported rather than silenced by inheritance.
    /// <para>
    /// It is a PER-INSTANCE option, not a class-wide constant, because this store has two kinds of consumer
    /// and only one of them owns an aggregate. <c>ScoringPass</c> counts every
    /// <see cref="Radar.Application.Storage.DurableWriteOutcome.Failed"/> result and emits one aggregated
    /// "{ScoreSnapshotsNotPersisted} score snapshot(s) could NOT be durably persisted" Warning, so those
    /// instances (the <c>AddFileScoreStore</c> registration and
    /// <see cref="StrategyScopedScoreSnapshotFileStoreFactory"/>) set
    /// <see cref="GracefulFileWriteFailureLogging.CallerAggregates"/>. <c>ReplayRunner</c> DISCARDS the
    /// write result and counts every point as written, so a replay-scoped store
    /// (<see cref="ReplayScopedScoreSnapshotFileStoreFactory"/>) deliberately keeps
    /// <see cref="GracefulFileWriteFailureLogging.Immediate"/>: its per-file Warning is the ONLY report a
    /// failed replay write has. (Replay's one aggregated Warning is spec 148's OVERWRITE warning — a
    /// different fact.)
    /// </para>
    /// <para>
    /// Internal because the mode itself is an Infrastructure concern
    /// (<see cref="GracefulFileWriteFailureLogging"/> is internal): only a construction site inside this
    /// assembly, which can see the caller's aggregate, is in a position to choose it.
    /// </para>
    /// </summary>
    internal GracefulFileWriteFailureLogging FailureLogging { get; init; } =
        GracefulFileWriteFailureLogging.Immediate;
}
