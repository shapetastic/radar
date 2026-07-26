namespace Radar.Domain.Scoring;

/// <remarks>
/// <see cref="ScoringConfigVersion"/> is DISTINCT from <see cref="ScoringVersion"/>:
/// <see cref="ScoringVersion"/> identifies the engine+formula identity, whereas
/// <see cref="ScoringConfigVersion"/> identifies the whole scoring-affecting generation
/// (formula + extractor rules + materiality tiers + scoring options). ONLY
/// <see cref="ScoringConfigVersion"/> gates cross-run comparability; a <c>null</c> value means
/// unknown/pre-stamp (an old on-disk file, or any snapshot written before this field existed) and is
/// therefore NEVER comparable.
/// <para>
/// <see cref="StrategyName"/> (spec 137) is the HUMAN-READABLE identity of the scoring strategy that
/// produced this snapshot, carried <b>alongside</b> — never instead of — the opaque
/// <see cref="ScoringConfigVersion"/> fingerprint (fingerprints are unreadable, and two strategies could in
/// principle resolve to the same effective config). It is deliberately NOT a comparability input and NOT a
/// fingerprint input. A <c>null</c> value means the primary/legacy strategy (an on-disk file written before
/// this field existed, or a snapshot produced outside the strategy composition).
/// </para>
/// </remarks>
public sealed record CompanyScoreSnapshot(
    Guid Id,
    Guid CompanyId,
    string ScoringVersion,
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore,
    string Explanation,
    string ComponentJson,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset CreatedAtUtc,
    string? ScoringConfigVersion = null,
    string? StrategyName = null);
