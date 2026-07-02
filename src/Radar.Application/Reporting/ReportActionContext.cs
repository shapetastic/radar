namespace Radar.Application.Reporting;

using Radar.Domain.Scoring;

/// <summary>
/// Inputs for deciding a company's weekly report label. <paramref name="Current"/> is the snapshot for
/// the reporting period; <paramref name="Previous"/> is the immediately-preceding snapshot for the same
/// company (null if none), used to detect an improving or deteriorating thesis. <paramref name="Previous"/>
/// is only acted on when <paramref name="PreviousComparable"/> is true — i.e. both snapshots were produced
/// by the same scoring generation; otherwise the policy falls back to its no-previous behaviour.
/// </summary>
public sealed record ReportActionContext(
    CompanyScoreSnapshot Current,
    CompanyScoreSnapshot? Previous,
    bool PreviousComparable = true);
