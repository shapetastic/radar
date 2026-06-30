namespace Radar.Application.Reporting;

/// <summary>A signal surfaced in the "needs review" section.</summary>
/// <param name="Summary">The extractor reason (what was found).</param>
/// <param name="ReviewReason">
/// The persisted reviewer's decision + summary (why it was flagged), or a stable fallback
/// when no <see cref="Radar.Domain.Signals.SignalReview"/> is stored for the signal.
/// </param>
public sealed record NeedsReviewSignalRef(
    Guid SignalId,
    Guid EvidenceId,
    string CompanyMention,
    string Summary,
    string ReviewReason);
