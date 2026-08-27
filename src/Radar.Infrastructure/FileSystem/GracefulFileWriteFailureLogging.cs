namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// How <see cref="GracefulFileWriter"/> reports a graceful write failure (spec 195 §1). A typed mode rather
/// than a boolean flag, deliberately: at a call site <c>true</c>/<c>false</c> says nothing about WHO owns the
/// reporting, and the whole point of this switch is that the responsibility moves rather than disappears.
/// </summary>
/// <remarks>
/// <para>
/// Spec 193 gave the pipeline passes an aggregated "N item(s) could not be durably persisted" Warning but
/// left the writer's per-file Warning in place, so a bad disk produced N detail lines PLUS the aggregate —
/// the aggregate was added, not substituted. This mode substitutes it at the two batch call sites.
/// </para>
/// <para>
/// <b><see cref="CallerAggregates"/> is legitimate ONLY where the caller owns a proven later aggregate.</b>
/// "Proven" means a test asserts that the aggregate is emitted for the same failures this suppresses — not
/// that someone intends to add one. Suppressing the Warning without that aggregate turns a graceful
/// degradation into a silent one, which is exactly the failure spec 193 §1 exists to prevent.
/// </para>
/// <para>
/// Today the sanctioned users are <see cref="FileSignalStore"/> (whose write failures are aggregated by
/// <c>CollectionPass</c>) and the <b>ScoringPass-owned instances</b> of
/// <see cref="FileScoreSnapshotStore"/> only. For that store the mode is a PER-INSTANCE option
/// (<see cref="FileScoreSnapshotStoreOptions.FailureLogging"/>), not a class-wide constant, because the
/// store has two kinds of consumer and only one of them owns an aggregate: <c>ScoringPass</c> counts every
/// failed write into one aggregated Warning, whereas <c>ReplayRunner</c> discards the write result and
/// counts every as-of point as written. A replay-scoped store therefore stays on <see cref="Immediate"/>,
/// where its per-file Warning is the only report a failed replay write has. Every other caller stays
/// <see cref="Immediate"/> too.
/// </para>
/// </remarks>
internal enum GracefulFileWriteFailureLogging
{
    /// <summary>
    /// Log one Warning (with the exception) per failed write. The default, and byte-for-byte the behaviour
    /// every caller had before spec 195.
    /// </summary>
    Immediate = 0,

    /// <summary>
    /// Suppress the per-file Warning because the caller emits one aggregated Warning for the whole pass. The
    /// attempted path is still logged, at Debug and without the exception stack trace, so the individual
    /// paths remain recoverable from a verbose run without N stack traces at Warning level.
    /// </summary>
    CallerAggregates = 1,
}
