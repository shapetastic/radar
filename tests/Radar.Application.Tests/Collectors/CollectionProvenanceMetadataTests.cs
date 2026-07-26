using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.TestSupport;

namespace Radar.Application.Tests.Collectors;

/// <summary>
/// Spec 146 — recording WHICH COLLECTOR retrieved a piece of evidence, which is the provenance a
/// <c>radar-formula-v9</c> collector channel selects on. Before this slice there was none:
/// <c>EvidenceItem.SourceType</c> is shared by several collectors and <c>SourceName</c> is the feed, not the
/// collector.
/// </summary>
public sealed class CollectionProvenanceMetadataTests
{
    private static CollectedEvidence Collected(IReadOnlyDictionary<string, string>? metadata = null) => new(
        SourceType: EvidenceSourceType.NewsArticle,
        SourceName: "Reuters",
        SourceUrl: "https://example.com/a",
        Title: "Acme wins contract",
        RawText: "Acme wins contract.",
        PublishedAt: null,
        CollectedAt: new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
        Metadata: metadata ?? new Dictionary<string, string>(StringComparer.Ordinal));

    [Fact]
    public void Stamp_RecordsTheCollectorName_WithoutMutatingTheInput()
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal) { ["quality"] = "high" };
        var collected = Collected(original);

        var stamped = CollectionProvenanceMetadata.Stamp(collected, "newssearch");

        Assert.Equal("newssearch", stamped.Metadata[CollectionProvenanceMetadata.MetadataKey]);
        // Existing metadata survives, and the caller's dictionary is untouched (a collector may hand out a
        // shared instance).
        Assert.Equal("high", stamped.Metadata["quality"]);
        Assert.DoesNotContain(CollectionProvenanceMetadata.MetadataKey, original.Keys);
        Assert.Single(original);
    }

    [Fact]
    public void Stamp_IsIdempotent_AndOverwritesADifferentRecordedCollector()
    {
        var once = CollectionProvenanceMetadata.Stamp(Collected(), "newssearch");

        Assert.Same(once, CollectionProvenanceMetadata.Stamp(once, "newssearch"));
        Assert.Equal(
            "gdelt",
            CollectionProvenanceMetadata.Stamp(once, "gdelt")
                .Metadata[CollectionProvenanceMetadata.MetadataKey]);
    }

    [Fact]
    public void Stamp_RejectsABlankCollectorName()
    {
        Assert.Throws<ArgumentException>(() => CollectionProvenanceMetadata.Stamp(Collected(), "   "));
        Assert.Throws<ArgumentNullException>(() => CollectionProvenanceMetadata.Stamp(null!, "x"));
    }

    [Fact]
    public void Read_RoundTripsTheStampedNameThroughTheMetadataEnvelope()
    {
        var stamped = CollectionProvenanceMetadata.Stamp(Collected(), "sec-form4");
        var metadataJson = EvidenceMetadata.Compose(stamped.Metadata, stamped.CompanyHints);

        var evidence = new EvidenceBuilder().WithMetadataJson(metadataJson).Build();

        Assert.Equal("sec-form4", CollectionProvenanceMetadata.Read(evidence));
        Assert.Equal("sec-form4", CollectionProvenanceMetadata.Read(metadataJson));
    }

    [Fact]
    public void Read_DegradesToNull_ForLegacyMissingAndMalformedMetadata()
    {
        // Accrued evidence has no collector key (never backfilled, by standing rule), so this must read as
        // "unrecorded" rather than throwing — a channel then simply consumes nothing from it.
        Assert.Null(CollectionProvenanceMetadata.Read((EvidenceItem?)null));
        Assert.Null(CollectionProvenanceMetadata.Read(new EvidenceBuilder().WithMetadataJson(null).Build()));
        Assert.Null(CollectionProvenanceMetadata.Read(new EvidenceBuilder().WithMetadataJson("").Build()));
        Assert.Null(CollectionProvenanceMetadata.Read(new EvidenceBuilder().WithMetadataJson("{ not json").Build()));
        Assert.Null(CollectionProvenanceMetadata.Read(
            new EvidenceBuilder().WithMetadataJson("{\"metadata\":{\"quality\":\"high\"}}").Build()));
        Assert.Null(CollectionProvenanceMetadata.Read(
            new EvidenceBuilder().WithMetadataJson("{\"metadata\":{\"collector\":\"   \"}}").Build()));
    }
}
