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
}
