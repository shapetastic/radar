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
    /// The effective resolved scoring config for this engine instance — the inputs the
    /// <c>ScoringConfigVersion</c> fingerprint hashes (engine + formula structure identity, every
    /// <see cref="ScoringWeights"/> value, and the attention tier-map descriptor), plus the resulting
    /// fingerprint. A pure accessor for the already-held config identity (no clock/IO/randomness, no
    /// scoring-math), for content-addressed persistence so a snapshot's stamp dereferences back to the
    /// weights that produced it (provenance completion, AD-10-as-amended).
    /// </summary>
    EffectiveScoringConfig EffectiveConfig { get; }
}
