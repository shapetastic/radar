namespace Radar.Domain.Scoring;

/// <remarks>
/// <see cref="ScoringConfigVersion"/> is DISTINCT from <see cref="ScoringVersion"/>:
/// <see cref="ScoringVersion"/> identifies the engine+formula identity, whereas
/// <see cref="ScoringConfigVersion"/> identifies the whole scoring-affecting generation
/// (formula + extractor rules + materiality tiers + scoring options). ONLY
/// <see cref="ScoringConfigVersion"/> gates cross-run comparability; a <c>null</c> value means
/// unknown/pre-stamp (an old on-disk file, or any snapshot written before this field existed) and is
/// therefore NEVER comparable.
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
    string? ScoringConfigVersion = null);
