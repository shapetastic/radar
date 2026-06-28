namespace Radar.Application.Collectors;

/// <summary>
/// Pre-persistence collection result. A collector produces these raw, un-normalized records; the
/// <see cref="CollectedEvidenceMapper"/> turns each into an immutable domain
/// <see cref="Radar.Domain.Evidence.EvidenceItem"/> (normalization, content hashing, quality parsing,
/// <c>SourceType</c> resolution). <c>Metadata</c> is a free-form provenance bag (e.g. the local
/// collector puts its <c>sourceFile</c> and declared <c>quality</c> here).
/// </summary>
public sealed record CollectedEvidence(
    string SourceType,
    string SourceName,
    string? SourceUrl,
    string Title,
    string RawText,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CollectedAt,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// Ticker/name hints supplied by a company-specific collector (e.g. an RSS feed bound to one
    /// company). Empty for generic sources. Carried through to resolution in slice 30; preserved on
    /// the evidence's MetadataJson for provenance.
    /// </summary>
    public IReadOnlyList<string> CompanyHints { get; init; } = [];
}
