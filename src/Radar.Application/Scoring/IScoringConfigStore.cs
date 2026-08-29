using Radar.Application.Storage;

namespace Radar.Application.Scoring;

/// <summary>
/// The Application seam for persisting the effective scoring config content-addressed by its fingerprint
/// (all file I/O lives in Infrastructure, AD-5). The store completes the spec-89 provenance chain: a
/// snapshot's <c>ScoringConfigVersion</c> stamp dereferences back to the exact weights that produced it.
/// <para>
/// It additionally keeps a small per-STRATEGY-NAME record of the fingerprint that name last resolved to
/// (spec 141). That record is what demotes the fingerprint from primary key to <b>tripwire</b>: strategies
/// are immutable by convention, so a name whose fingerprint moved was edited in place, and
/// <see cref="StrategyIdentityGuard"/> fails the run fast instead of silently starting a second, identically
/// named series. It is a separate, mutable, per-name record — deliberately NOT part of the immutable
/// content-addressed config file, whose contents are fixed by its own fingerprint.
/// </para>
/// </summary>
public interface IScoringConfigStore
{
    /// <summary>
    /// Insert-if-new (AD-1-style immutable): writes the effective config to
    /// <c>{RootDirectory}/{config.Fingerprint}.json</c> ONLY if no file for that fingerprint exists yet — a
    /// given fingerprint's config is by definition fixed, so the same config always yields the same
    /// content. Best-effort (AD-8): a disk failure logs and never aborts the run (the snapshot still
    /// carries the fingerprint) — and the outcome is REPORTED (spec 201 §1): an already-existing file is
    /// <see cref="DurableWriteOutcome.Written"/> (the content IS on disk), a write or serialization failure is
    /// <see cref="DurableWriteOutcome.Failed"/>, so a stamp whose content-addressed file never landed is
    /// counted by the caller rather than silently dereferencing to nothing.
    /// </summary>
    Task<DurableWriteResult> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct);

    /// <summary>
    /// The fingerprint last recorded for this strategy NAME, or <c>null</c> when it has never been recorded
    /// (a brand-new strategy) or the record could not be read. Graceful-degrade (AD-8): an unreadable or
    /// malformed record logs a warning and reads as <c>null</c> — a missing record must not fail a run, and
    /// "cannot tell" must never be reported as "changed" (that would trip the guard on a disk hiccup).
    /// <see cref="OperationCanceledException"/> still propagates.
    /// </summary>
    Task<string?> ReadStrategyFingerprintAsync(string strategyName, CancellationToken ct);

    /// <summary>
    /// Records (upsert) the fingerprint this strategy NAME currently resolves to. Best-effort like every
    /// other file store: a disk failure logs and continues — and reports the outcome (spec 201 §1), because a
    /// record that never landed means the tripwire has nothing to compare against next run.
    /// </summary>
    Task<DurableWriteResult> RecordStrategyFingerprintAsync(
        string strategyName, string fingerprint, CancellationToken ct);
}
