using Radar.Application.Collectors;
using Radar.Application.Reporting;

namespace Radar.Application.Pipeline;

/// <summary>
/// Deterministic summary of one pipeline run. Counts are observational only — provenance lives in the
/// persisted evidence/signals/snapshots/report, not here. The scalar <see cref="SourcesChecked"/> and
/// <see cref="SourcesFailed"/> mirror the corresponding fields on <see cref="Collection"/>, which also
/// carries the per-source failure list.
/// <para>
/// <see cref="RunId"/> and <see cref="StrategySections"/> (spec 179 §2) are the additive in-process
/// transport for the news-risk shadow step: the durable <c>PipelineRunRecord.Id</c> this run wrote, and the
/// EXACT spec-150/176 section instances the report builder produced. Both trailing and defaulted, so the
/// collect-only and score-only runners (which the shadow step is never registered alongside) compile and
/// behave unchanged with <c>null</c>.
/// </para>
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
    CollectionSummary Collection,
    Guid? RunId = null,
    IReadOnlyList<StrategyReportSection>? StrategySections = null);
