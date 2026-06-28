namespace Radar.Application.Reporting;

/// <summary>One piece of evidence behind a company entry (provenance for display).</summary>
public sealed record ReportEvidenceRef(
    Guid EvidenceId,
    Guid SignalId,
    string SourceName,
    string? SourceUrl,
    string Title,
    string ContributionReason);
