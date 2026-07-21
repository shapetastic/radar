namespace Radar.Application.Reporting;

using Radar.Domain.Companies;
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
    bool PreviousScoringChanged = false,
    // Curated "how followed already" tier of the company (seed metadata, never price-derived — AD-14).
    // Carried purely so the report can SHOW the notedness inputs behind the Opportunity discount; the
    // renderer never recomputes the formula's discount from it. Defaults to the enum's own fail-safe
    // Small so existing construction sites keep compiling.
    FollowingTier FollowingTier = FollowingTier.Small);
