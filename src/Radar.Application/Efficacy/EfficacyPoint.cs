namespace Radar.Application.Efficacy;

/// <summary>
/// One joined efficacy point: a score snapshot's numeric components paired (no look-ahead) to the price bar
/// at-or-before its date. VALIDATION/RESEARCH data only (AD-14) — never a scoring input.
/// </summary>
public sealed record EfficacyPoint(
    DateOnly ScoreDate,
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore,
    string? ScoringConfigVersion,   // the fingerprint segment key (null = pre-stamp/unknown)
    DateOnly? PriceAsOfDate,        // the actual bar date used (at-or-before ScoreDate), null if unpaired
    decimal? PriceClose,
    decimal? PriceAdjClose);
