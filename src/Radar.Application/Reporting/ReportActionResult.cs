namespace Radar.Application.Reporting;

using Radar.Domain.Reports;

/// <summary>A chosen allowed label plus a deterministic, advice-free rationale.</summary>
public sealed record ReportActionResult(RadarReportAction Action, string Rationale);
