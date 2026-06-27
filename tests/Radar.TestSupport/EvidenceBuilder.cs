using Radar.Domain.Evidence;

namespace Radar.TestSupport;

public sealed class EvidenceBuilder
{
    private Guid _id = Guid.NewGuid();
    private EvidenceSourceType _sourceType = EvidenceSourceType.PressRelease;
    private string _sourceName = "Acme Newsroom";
    private string? _sourceUrl = "https://example.com/acme";
    private string _title = "Untitled";
    private string? _summary = "A summary.";
    private string _rawText = "Acme made an announcement today.";
    private string _contentHash = "hash-1";
    private DateTimeOffset? _publishedAtUtc;
    private DateTimeOffset _collectedAtUtc = new(2026, 1, 16, 12, 0, 0, TimeSpan.Zero);
    private EvidenceQuality _quality = EvidenceQuality.High;
    private string? _metadataJson;

    public EvidenceBuilder WithId(Guid id) { _id = id; return this; }
    public EvidenceBuilder WithSourceType(EvidenceSourceType v) { _sourceType = v; return this; }
    public EvidenceBuilder WithSourceName(string v) { _sourceName = v; return this; }
    public EvidenceBuilder WithSourceUrl(string? v) { _sourceUrl = v; return this; }
    public EvidenceBuilder WithTitle(string v) { _title = v; return this; }
    public EvidenceBuilder WithSummary(string? v) { _summary = v; return this; }
    public EvidenceBuilder WithRawText(string v) { _rawText = v; return this; }
    public EvidenceBuilder WithContentHash(string v) { _contentHash = v; return this; }
    public EvidenceBuilder WithPublishedAtUtc(DateTimeOffset? v) { _publishedAtUtc = v; return this; }
    public EvidenceBuilder WithCollectedAtUtc(DateTimeOffset v) { _collectedAtUtc = v; return this; }
    public EvidenceBuilder WithQuality(EvidenceQuality v) { _quality = v; return this; }
    public EvidenceBuilder WithMetadataJson(string? v) { _metadataJson = v; return this; }

    public EvidenceItem Build() => new(
        Id: _id,
        SourceType: _sourceType,
        SourceName: _sourceName,
        SourceUrl: _sourceUrl,
        Title: _title,
        Summary: _summary,
        RawText: _rawText,
        ContentHash: _contentHash,
        PublishedAtUtc: _publishedAtUtc,
        CollectedAtUtc: _collectedAtUtc,
        Quality: _quality,
        MetadataJson: _metadataJson);
}
