using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.TestSupport;

namespace Radar.Application.Tests.Collectors;

/// <summary>
/// Spec 151 — the attribution VALUE and the default (recorded-only) resolver.
/// <para>
/// The assertion that carries the acceptance criterion is
/// <see cref="Recorded_And_Inferred_AreDistinguishable_StructurallyNotByConvention"/>: "inferred ≠ recorded"
/// must be a property of the type, not a documented habit.
/// </para>
/// </summary>
public sealed class CollectorAttributionTests
{
    private static EvidenceItem EvidenceWith(params (string Key, string Value)[] metadata) =>
        new EvidenceBuilder()
            .WithMetadataJson(EvidenceMetadata.Compose(
                metadata.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal), []))
            .Build();

    // ---- the value type ------------------------------------------------------------------------------

    [Fact]
    public void Recorded_And_Inferred_AreDistinguishable_StructurallyNotByConvention()
    {
        // Same collector, two provenances. They must NOT compare equal, and neither may be mistaken for the
        // other by reading the name — which is precisely what a bare string? could not express.
        var recorded = CollectorAttribution.Recorded("newssearch");
        var inferred = CollectorAttribution.Inferred("newssearch");

        Assert.Equal("newssearch", recorded.CollectorName);
        Assert.Equal("newssearch", inferred.CollectorName);
        Assert.Equal(CollectorAttributionSource.Recorded, recorded.Source);
        Assert.Equal(CollectorAttributionSource.Inferred, inferred.Source);
        Assert.NotEqual(recorded, inferred);
    }

    [Fact]
    public void Default_IsUnattributed_SoTheInvariantHoldsEvenForAZeroedStruct()
    {
        // CollectorAttributionSource.Unattributed is pinned to 0 exactly so this is true. If a future edit
        // reorders the enum, default(CollectorAttribution) becomes a nameless "Recorded" — an attributed value
        // nobody wrote — and this fails.
        CollectorAttribution zeroed = default;

        Assert.Equal(CollectorAttribution.Unattributed, zeroed);
        Assert.Null(zeroed.CollectorName);
        Assert.Equal(CollectorAttributionSource.Unattributed, zeroed.Source);
        Assert.False(zeroed.IsAttributed);
    }

    [Fact]
    public void AttributedValues_ReportIsAttributed()
    {
        Assert.True(CollectorAttribution.Recorded("sec-form4").IsAttributed);
        Assert.True(CollectorAttribution.Inferred("sec-form4").IsAttributed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AttributedValue_WithoutAName_IsNotExpressible(string? name)
    {
        // The invariant's other half: "attributed but nameless" would let a consumer see IsAttributed == true
        // and still get null when it asked WHICH collector.
        Assert.ThrowsAny<ArgumentException>(() => CollectorAttribution.Recorded(name!));
        Assert.ThrowsAny<ArgumentException>(() => CollectorAttribution.Inferred(name!));
    }

    [Fact]
    public void SameSourceAndName_CompareEqual()
    {
        Assert.Equal(CollectorAttribution.Recorded("rss"), CollectorAttribution.Recorded("rss"));
        Assert.NotEqual(CollectorAttribution.Recorded("rss"), CollectorAttribution.Recorded("news"));
    }

    // ---- the DEFAULT resolver: behaviourally identical to the pre-151 inline metadata read --------------

    [Fact]
    public void RecordedOnlyResolver_ReadsTheRecordedCollector()
    {
        var evidence = EvidenceWith((CollectionProvenanceMetadata.MetadataKey, "sec-13dg"));

        var resolved = RecordedOnlyCollectorAttributionResolver.Instance.Resolve(evidence);

        Assert.Equal(CollectorAttribution.Recorded("sec-13dg"), resolved);
    }

    [Fact]
    public void RecordedOnlyResolver_NeverInfers_EvenWhenTheEvidenceIsPerfectlyIdentifiable()
    {
        // This news article carries the newssearch collector's exclusive marker, so the spec-151 inference
        // would name it confidently. The DEFAULT resolver must still answer "unattributed" — that is what
        // makes the inference opt-in and what keeps pre-151 scoring byte-identical.
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithMetadataJson(EvidenceMetadata.Compose(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["newsSearchFeedUrl"] = "https://news.google.com/rss/search?q=acme",
                },
                []))
            .Build();

        Assert.Equal(
            CollectorAttribution.Unattributed,
            RecordedOnlyCollectorAttributionResolver.Instance.Resolve(evidence));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"metadata\":{\"collector\":\"   \"}}")]
    [InlineData("{\"metadata\":{}}")]
    public void RecordedOnlyResolver_DegradesToUnattributed_NeverThrows(string? metadataJson)
    {
        var evidence = new EvidenceBuilder().WithMetadataJson(metadataJson).Build();

        Assert.Equal(
            CollectorAttribution.Unattributed,
            RecordedOnlyCollectorAttributionResolver.Instance.Resolve(evidence));
    }

    [Fact]
    public void RecordedOnlyResolver_HandlesNullEvidence()
    {
        Assert.Equal(
            CollectorAttribution.Unattributed,
            RecordedOnlyCollectorAttributionResolver.Instance.Resolve(null));
    }
}
