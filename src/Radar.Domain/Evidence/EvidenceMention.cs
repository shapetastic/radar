namespace Radar.Domain.Evidence;

public sealed record EvidenceMention(
    Guid Id,
    Guid EvidenceId,
    string MentionText,
    Guid? ResolvedCompanyId,
    decimal ResolutionConfidence,
    string? ResolutionReason,
    DateTimeOffset CreatedAtUtc);
