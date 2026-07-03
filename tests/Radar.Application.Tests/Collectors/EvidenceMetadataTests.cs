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
}
