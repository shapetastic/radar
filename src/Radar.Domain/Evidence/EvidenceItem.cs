namespace Radar.Domain.Evidence;

public sealed record EvidenceItem(
    Guid Id,
    EvidenceSourceType SourceType,
    string SourceName,
    string? SourceUrl,
    string Title,
    string? Summary,
    string RawText,
    string ContentHash,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset CollectedAtUtc,
    EvidenceQuality Quality,
    string? MetadataJson);
