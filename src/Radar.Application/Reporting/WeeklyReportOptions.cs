namespace Radar.Application.Reporting;

/// <summary>
/// Operational weekly-report parameters (NOT label thresholds — those live in IReportActionPolicy).
/// </summary>
public sealed class WeeklyReportOptions
{
    /// <summary>Reporting period length. Default 7 days per the pipeline spec ("weekly").</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Report type label stored on the RadarReport. Default "Weekly".</summary>
    public string ReportType { get; init; } = "Weekly";

    /// <summary>Maximum number of company entries to include (highest opportunity first). Default 25.</summary>
    public int MaxItems { get; init; } = 25;

    /// <summary>
    /// How many recent runs to show in the report's "Recent runs" footer. Default 5. A non-positive
    /// value yields no footer (the run store returns an empty list for a non-positive count).
    /// </summary>
    public int RecentRunsInReport { get; init; } = 5;
}
