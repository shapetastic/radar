namespace Radar.Application.Pipeline;

/// <summary>
/// Deterministic summary of one pipeline run. Counts are observational only — provenance lives in the
/// persisted evidence/signals/snapshots/report, not here.
/// </summary>
public sealed record RadarPipelineResult(
    int EvidenceCollected,
    int EvidenceNew,
    int SignalsExtracted,
    int SignalsValid,
    int SignalsApproved,
    int SignalsNeedingReview,
    int CompaniesScored,
    Guid? ReportId);
