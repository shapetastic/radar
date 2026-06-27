namespace Radar.Domain.Reports;

public sealed record RadarReportItem(
    Guid Id,
    Guid ReportId,
    Guid CompanyId,
    Guid ScoreSnapshotId,
    RadarReportAction SuggestedAction,
    string Summary,
    int Rank);
