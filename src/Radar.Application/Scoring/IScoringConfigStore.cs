namespace Radar.Application.Scoring;

/// <summary>
/// The Application seam for persisting the effective scoring config content-addressed by its fingerprint
/// (all file I/O lives in Infrastructure, AD-5). The store completes the spec-89 provenance chain: a
/// snapshot's <c>ScoringConfigVersion</c> stamp dereferences back to the exact weights that produced it.
/// </summary>
public interface IScoringConfigStore
{
    /// <summary>
    /// Insert-if-new (AD-1-style immutable): writes the effective config to
    /// <c>{RootDirectory}/{config.Fingerprint}.json</c> ONLY if no file for that fingerprint exists yet — a
    /// given fingerprint's config is by definition fixed, so the same config always yields the same
    /// content. Best-effort (AD-8): a disk failure logs + returns the attempted path, never aborts the
    /// run (the snapshot still carries the fingerprint). Returns the (existing or written) path.
    /// </summary>
    Task<string> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct);
}
