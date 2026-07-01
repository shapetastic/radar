namespace Radar.Application.Pipeline;

/// <summary>
/// Immutable, durable record of one completed pipeline run: the run instant, which collectors ran, the
/// run's observational counts, and the generated report id (if any). It is a run-observability projection
/// of <see cref="RadarPipelineResult"/> — NOT a Domain aggregate — persisted once per run to build a
/// run history for week-over-week comparison. The counts are observational only; provenance still lives
/// in the persisted evidence/signals/snapshots/report, not here. All temporal fields are UTC; the run is
/// stamped with the run's single <c>asOfUtc</c> instant (one run, one instant, AD-7).
/// </summary>
public sealed record PipelineRunRecord(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Collectors,
    int EvidenceCollected,
    int EvidenceNew,
    int SignalsExtracted,
    int SignalsValid,
    int SignalsApproved,
    int SignalsNeedingReview,
    int CompaniesScored,
    int SourcesChecked,
    int SourcesFailed,
    Guid? ReportId);
