namespace Radar.Domain.Signals;

public sealed record Signal(
    Guid Id,
    Guid EvidenceId,
    Guid? CompanyId,
    string CompanyMention,
    SignalType Type,
    SignalDirection Direction,
    int Strength,
    int Novelty,
    decimal Confidence,
    string SupportingExcerpt,
    string Reason,
    SignalReviewStatus ReviewStatus,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset CreatedAtUtc);
