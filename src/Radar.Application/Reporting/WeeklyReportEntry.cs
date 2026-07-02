namespace Radar.Application.Reporting;

using Radar.Domain.Reports;
using Radar.Domain.Scoring;

/// <summary>One company's row in the weekly report, carrying its snapshot id and evidence.</summary>
public sealed record WeeklyReportEntry(
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    Guid ScoreSnapshotId,
    CompanyScoreSnapshot Snapshot,
    RadarReportAction Action,
    string Rationale,
    int Rank,
    IReadOnlyList<ReportEvidenceRef> Evidence,
    IReadOnlyList<ReportSignalRef> Signals,
    int? PreviousOpportunityScore = null,
    int? PreviousTrajectoryScore = null,
    bool PreviousScoringChanged = false);
