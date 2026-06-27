namespace Radar.Application.Scoring;

/// <summary>The five MVP component scores, each constrained to the inclusive range 0..100.</summary>
public sealed record ScoreComponents(
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore);
