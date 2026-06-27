namespace Radar.Domain.Signals;

public sealed record SignalReview(
    Guid Id,
    Guid SignalId,
    string ReviewerName,
    SignalReviewDecision Decision,
    string Summary,
    string? IssuesJson,
    DateTimeOffset ReviewedAtUtc);
