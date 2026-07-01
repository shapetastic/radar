namespace Radar.Application.Reporting;

/// <summary>
/// Minimal presentation projection of one <see cref="Radar.Application.Pipeline.PipelineRunRecord"/>
/// for the weekly report's "Recent runs" footer. Observational metadata only (no labels, no advice):
/// the run instant, which collectors ran, and a glance at the run's counts. The full record stays on
/// disk; this carries just what the footer renders.
/// </summary>
public sealed record RecentRunSummary(
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Collectors,
    int EvidenceNew,
    int SignalsApproved,
    int CompaniesScored,
    int SourcesChecked,
    int SourcesFailed);
