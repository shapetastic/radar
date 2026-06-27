using Radar.Domain.Scoring;

namespace Radar.Application.Scoring;

/// <summary>The persisted snapshot together with the evidence links that trace it to signals/evidence.</summary>
public sealed record CompanyScoreResult(
    CompanyScoreSnapshot Snapshot,
    IReadOnlyList<ScoreEvidenceLink> Links);
