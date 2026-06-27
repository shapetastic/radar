namespace Radar.Domain.Reports;

public sealed record RadarReport(
    Guid Id,
    string ReportType,
    string Title,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    string MarkdownContent,
    DateTimeOffset CreatedAtUtc);
