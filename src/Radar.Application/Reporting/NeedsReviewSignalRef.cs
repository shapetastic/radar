namespace Radar.Application.Reporting;

/// <summary>A signal surfaced in the "needs review" section.</summary>
public sealed record NeedsReviewSignalRef(
    Guid SignalId,
    Guid EvidenceId,
    string CompanyMention,
    string Summary);
