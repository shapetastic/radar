namespace Radar.Domain.Scoring;

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
    DateTimeOffset CreatedAtUtc);
