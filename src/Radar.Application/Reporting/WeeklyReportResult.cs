namespace Radar.Application.Reporting;

using Radar.Domain.Reports;

/// <summary>The persisted report plus the items that trace it to score snapshots.</summary>
public sealed record WeeklyReportResult(RadarReport Report, IReadOnlyList<RadarReportItem> Items);
