namespace Radar.Application.Scoring;

/// <summary>
/// Stage 6 engine: computes and persists a <c>CompanyScoreSnapshot</c> for one company over the
/// recent-signal window ending at <paramref name="windowEndUtc"/>, plus the <c>ScoreEvidenceLink</c>
/// rows tracing it back to the contributing signals and evidence.
/// </summary>
public interface IScoringEngine
{
    Task<CompanyScoreResult> ScoreCompanyAsync(
        Guid companyId, DateTimeOffset windowEndUtc, CancellationToken ct);

    /// <summary>
    /// Spec 203 §4: performs ONLY the raw store reads this engine would make for <paramref name="companyId"/>
    /// at <paramref name="windowEndUtc"/> — the company's signals, the previous/velocity window and the
    /// evidence behind every window/known-at/Approved signal — so they can be handed to several strategy
    /// engines. No scoring, no filtering by strategy, no persistence.
    /// </summary>
    Task<CompanyScoringReads> ReadCompanyAsync(
        Guid companyId, DateTimeOffset windowEndUtc, CancellationToken ct);

    /// <summary>
    /// Spec 203 §4: scores from already-materialised reads. Byte-identical to the two-argument overload over
    /// the same stores — that overload IS <c>ScoreCompanyAsync(await ReadCompanyAsync(…), ct)</c>. Throws
    /// <see cref="ArgumentException"/> when <see cref="CompanyScoringReads.Window"/> differs from this
    /// engine's window: reads sliced for another window would score silently wrong.
    /// </summary>
    Task<CompanyScoreResult> ScoreCompanyAsync(CompanyScoringReads reads, CancellationToken ct);

    /// <summary>
    /// The effective resolved scoring config for this engine instance — the inputs the
    /// <c>ScoringConfigVersion</c> fingerprint hashes (engine + formula structure identity, every
    /// <see cref="ScoringWeights"/> value, and the attention tier-map descriptor), plus the resulting
    /// fingerprint. A pure accessor for the already-held config identity (no clock/IO/randomness, no
    /// scoring-math), for content-addressed persistence so a snapshot's stamp dereferences back to the
    /// weights that produced it (provenance completion, AD-10-as-amended).
    /// </summary>
    EffectiveScoringConfig EffectiveConfig { get; }
}
