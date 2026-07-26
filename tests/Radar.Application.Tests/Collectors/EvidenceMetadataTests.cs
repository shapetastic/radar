using System.Text.Json;

using Radar.Application.Collectors;

namespace Radar.Application.Tests.Collectors;

public sealed class EvidenceMetadataTests
{
    [Fact]
    public void TryRead_WellFormedEnvelope_ReturnsTrueWithMetadataAndHints()
    {
        const string json =
            """{"metadata":{"form":"8-K","accessionNumber":"0001-25-000001"},"companyHints":["Acme","Northwind"]}""";

        var ok = EvidenceMetadata.TryRead(json, out var metadata, out var hints);

        Assert.True(ok);
        Assert.Equal(2, metadata.Count);
        Assert.Equal("8-K", metadata["form"]);
        Assert.Equal("0001-25-000001", metadata["accessionNumber"]);
        Assert.Equal(new[] { "Acme", "Northwind" }, hints);
    }

    [Fact]
    public void TryRead_MixedValueKinds_KeepsOnlyStringMetadataAndUnaffectedHints()
    {
        const string json =
            """
            {
              "metadata": {
                "form": "8-K",
                "count": 42,
                "active": true,
                "nothing": null,
                "nested": { "a": "b" },
                "items": "2.02"
              },
              "companyHints": ["Acme"]
            }
            """;

        var ok = EvidenceMetadata.TryRead(json, out var metadata, out var hints);

        Assert.True(ok);
        Assert.Equal(2, metadata.Count);
        Assert.Equal("8-K", metadata["form"]);
        Assert.Equal("2.02", metadata["items"]);
        Assert.False(metadata.ContainsKey("count"));
        Assert.False(metadata.ContainsKey("active"));
        Assert.False(metadata.ContainsKey("nothing"));
        Assert.False(metadata.ContainsKey("nested"));
        Assert.Equal(new[] { "Acme" }, hints);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("[1, 2, 3]")]      // valid JSON but non-Object root
    [InlineData("\"a string\"")]   // valid JSON but non-Object root
    public void TryRead_NullBlankMalformedOrNonObjectRoot_ReturnsFalseWithEmptyProjections(string? json)
    {
        var ok = EvidenceMetadata.TryRead(json, out var metadata, out var hints);

        Assert.False(ok);
        Assert.Empty(metadata);
        Assert.Empty(hints);
    }

    [Fact]
    public void TryRead_MissingNodes_ReturnsTrueWithEmptyProjections()
    {
        var ok = EvidenceMetadata.TryRead("{}", out var metadata, out var hints);

        Assert.True(ok);
        Assert.Empty(metadata);
        Assert.Empty(hints);
    }

    [Fact]
    public void TryRead_WrongKindNodes_ReturnsTrueWithEmptyProjections()
    {
        // metadata is an array; companyHints is an object — both wrong kinds. A well-formed root Object
        // still returns true with empty projections rather than throwing.
        const string json = """{"metadata":[1,2],"companyHints":{"a":"b"}}""";

        var ok = EvidenceMetadata.TryRead(json, out var metadata, out var hints);

        Assert.True(ok);
        Assert.Empty(metadata);
        Assert.Empty(hints);
    }

    [Fact]
    public void TryRead_HintsArrayWithNonStringElements_KeepsOnlyStrings()
    {
        const string json = """{"companyHints":["Acme",42,null,"Northwind"]}""";

        var ok = EvidenceMetadata.TryRead(json, out var metadata, out var hints);

        Assert.True(ok);
        Assert.Empty(metadata);
        Assert.Equal(new[] { "Acme", "Northwind" }, hints);
    }

    [Fact]
    public void TryRead_UsesOrdinalKeys()
    {
        const string json = """{"metadata":{"Form":"8-K"}}""";

        var ok = EvidenceMetadata.TryRead(json, out var metadata, out _);

        Assert.True(ok);
        Assert.True(metadata.ContainsKey("Form"));
        Assert.False(metadata.ContainsKey("form")); // ordinal, case-sensitive
    }

    [Fact]
    public void TryRead_RoundTripsEnvelopeAuthoredLikeTheMapper()
    {
        // Build the envelope EXACTLY as CollectedEvidenceMapper does so this asserts author and reader agree.
        var sourceMetadata = new Dictionary<string, string>
        {
            ["form"] = "8-K",
            ["items"] = "2.02",
            ["awardAmount"] = "1500000.50",
        };
        var sourceHints = new List<string> { "Acme", "Northwind" };

        var json = JsonSerializer.Serialize(new { metadata = sourceMetadata, companyHints = sourceHints });

        var ok = EvidenceMetadata.TryRead(json, out var metadata, out var hints);

        Assert.True(ok);
        Assert.Equal(sourceMetadata.Count, metadata.Count);
        foreach (var (key, value) in sourceMetadata)
        {
            Assert.Equal(value, metadata[key]);
        }

        Assert.Equal(sourceHints, hints);
    }

    // -------------------------------------------------------------------------------------------------
    // Compose — the envelope WRITER (spec 142). Shared by the mapper and the durable hydration path so
    // a hydrated EvidenceItem.MetadataJson is byte-identical to the one collection produced.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Compose_ProducesExactlyTheEnvelopeTheMapperUsedToWriteInline()
    {
        // The pre-142 mapper serialized this anonymous shape with default options. Compose must be
        // byte-identical to it, or every previously-written MetadataJson would change meaning.
        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string>
        {
            ["quality"] = "High",
            ["form"] = "8-K",
        };
        IReadOnlyList<string> hints = ["ACME", "Northwind"];

        var expected = JsonSerializer.Serialize(new { metadata, companyHints = hints });

        Assert.Equal(expected, EvidenceMetadata.Compose(metadata, hints));
    }

    [Fact]
    public void Compose_ThenTryRead_RoundTrips()
    {
        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string>
        {
            ["a"] = "1",
            ["b"] = "two",
        };
        IReadOnlyList<string> hints = ["X"];

        var ok = EvidenceMetadata.TryRead(
            EvidenceMetadata.Compose(metadata, hints), out var readMetadata, out var readHints);

        Assert.True(ok);
        Assert.Equal(metadata, readMetadata);
        Assert.Equal(hints, readHints);
    }

    [Fact]
    public void Compose_PreservesKeyOrder_SoTheEnvelopeIsStable()
    {
        // Dictionary insertion order is what the on-disk `metadata` node records, and what the hydration
        // path replays back in document order — so composing twice from the same source is deterministic.
        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string>
        {
            ["z"] = "1",
            ["a"] = "2",
            ["m"] = "3",
        };

        Assert.Equal(
            """{"metadata":{"z":"1","a":"2","m":"3"},"companyHints":[]}""",
            EvidenceMetadata.Compose(metadata, []));
    }

    // -------------------------------------------------------------------------------------------------
    // ReadMetadataObject — the SAME projection TryRead applies, exposed for the durable raw-evidence store
    // whose on-disk shape stores `metadata` and `companyHints` as separate nodes.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ReadMetadataObject_ProjectsStringValuedPropertiesInDocumentOrder()
    {
        using var doc = JsonDocument.Parse("""{"quality":"High","count":7,"form":"8-K","nested":{"x":"y"}}""");

        var projected = EvidenceMetadata.ReadMetadataObject(doc.RootElement);

        Assert.Equal(["quality", "form"], projected.Keys.ToArray());
        Assert.Equal("High", projected["quality"]);
        Assert.Equal("8-K", projected["form"]);
    }

    [Fact]
    public void ReadMetadataObject_NonObjectElement_ProjectsEmpty()
    {
        using var doc = JsonDocument.Parse("[1,2,3]");

        Assert.Empty(EvidenceMetadata.ReadMetadataObject(doc.RootElement));
        Assert.Empty(EvidenceMetadata.ReadMetadataObject(default));
    }
}
