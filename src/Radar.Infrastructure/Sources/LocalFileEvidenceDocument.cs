namespace Radar.Infrastructure.Sources;

/// <summary>
/// Internal JSON DTO for a single local-file evidence document. All members are nullable so
/// that malformed or incomplete files can be detected and skipped rather than throwing.
/// </summary>
internal sealed record LocalFileEvidenceDocument(
    string? SourceName,
    string? SourceUrl,
    string? Title,
    string? Summary,
    DateTimeOffset? PublishedAtUtc,
    string? RawText);
