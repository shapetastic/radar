using Radar.Application.Collectors;

namespace Radar.Application.Pipeline;

/// <summary>
/// Deterministic summary of one pipeline run. Counts are observational only — provenance lives in the
/// persisted evidence/signals/snapshots/report, not here. The scalar <see cref="SourcesChecked"/> and
/// <see cref="SourcesFailed"/> mirror the corresponding fields on <see cref="Collection"/>, which also
/// carries the per-source failure list.
/// </summary>
public sealed record RadarPipelineResult(
    int EvidenceCollected,
    int EvidenceNew,
    int SignalsExtracted,
    int SignalsValid,
    int SignalsApproved,
    int SignalsNeedingReview,
    int CompaniesScored,
    Guid? ReportId,
    int SourcesChecked,
    int SourcesFailed,
    CollectionSummary Collection);
