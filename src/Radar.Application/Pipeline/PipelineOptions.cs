namespace Radar.Application.Pipeline;

/// <summary>
/// Operational pipeline parameters (NOT scoring weights or label thresholds). The scoring window and
/// report period live on ScoringOptions / WeeklyReportOptions respectively; this only toggles whether a
/// run finishes by building the Stage 7 report.
/// </summary>
public sealed class PipelineOptions
{
    /// <summary>When true (default) the run ends by generating the weekly report (Stage 7).</summary>
    public bool GenerateReport { get; init; } = true;
}
