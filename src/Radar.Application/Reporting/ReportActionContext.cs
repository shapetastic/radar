namespace Radar.Application.Reporting;

using Radar.Domain.Scoring;

/// <summary>
/// Inputs for deciding a company's weekly report label. <paramref name="Current"/> is the snapshot for
/// the reporting period; <paramref name="Previous"/> is the immediately-preceding snapshot for the same
/// company (null if none), used to detect an improving or deteriorating thesis.
/// </summary>
public sealed record ReportActionContext(
    CompanyScoreSnapshot Current,
    CompanyScoreSnapshot? Previous);
