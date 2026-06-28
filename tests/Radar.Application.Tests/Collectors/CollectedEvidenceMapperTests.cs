using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.Evidence;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.Collectors;

public sealed class CollectedEvidenceMapperTests
{
    private static CollectedEvidenceMapper CreateMapper() =>
        new(new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance);

    private static CollectedEvidence Build(
        string sourceType = "local_file",
        string sourceName = "Northwind Newsroom",
        string? sourceUrl = "https://example.com/nw",
        string title = "Northwind Robotics customer win",
        string rawText = "Northwind Robotics announced a major new customer win today.",
        DateTimeOffset? publishedAt = null,
        DateTimeOffset? collectedAt = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyList<string>? companyHints = null) =>
        new(
            SourceType: sourceType,
            SourceName: sourceName,
            SourceUrl: sourceUrl,
            Title: title,
            RawText: rawText,
            PublishedAt: publishedAt,
            CollectedAt: collectedAt ?? new DateTimeOffset(2026, 2, 8, 12, 0, 0, TimeSpan.Zero),
            Metadata: metadata ?? new Dictionary<string, string>())
        {
            CompanyHints = companyHints ?? [],
        };

    [Fact]
    public void ToEvidenceItem_NormalizesTextAndHash_PreservingProvenance()
    {
        const string title = "Northwind Robotics customer win";
        const string rawText = "Northwind Robotics announced a major new customer win today.";
        var mapper = CreateMapper();

        var item = mapper.ToEvidenceItem(Build(title: title, rawText: rawText));

        var expected = new EvidenceNormalizer().Normalize(title, rawText);
        Assert.Equal(expected.NormalizedText, item.RawText);
        Assert.Equal(expected.ContentHash, item.ContentHash);
    }

    [Theory]
    [InlineData("local_file", EvidenceSourceType.LocalFile)]
    [InlineData("press_release", EvidenceSourceType.PressRelease)]
    [InlineData("wat", EvidenceSourceType.Manual)]
    public void ToEvidenceItem_ResolvesSourceType(string sourceType, EvidenceSourceType expected)
    {
        var mapper = CreateMapper();

        var item = mapper.ToEvidenceItem(Build(sourceType: sourceType));

        Assert.Equal(expected, item.SourceType);
    }

    [Fact]
    public void ToEvidenceItem_QualityFromMetadata_MapsCaseInsensitively()
    {
        var mapper = CreateMapper();

        var item = mapper.ToEvidenceItem(
            Build(metadata: new Dictionary<string, string> { ["quality"] = "High" }));

        Assert.Equal(EvidenceQuality.High, item.Quality);
    }

    [Fact]
    public void ToEvidenceItem_MissingQualityKey_DefaultsToUnknown()
    {
        var mapper = CreateMapper();

        var item = mapper.ToEvidenceItem(Build(metadata: new Dictionary<string, string>()));

        Assert.Equal(EvidenceQuality.Unknown, item.Quality);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("4")]
    public void ToEvidenceItem_BlankOrDigitOnlyQuality_DefaultsToUnknown(string quality)
    {
        var mapper = CreateMapper();

        var item = mapper.ToEvidenceItem(
            Build(metadata: new Dictionary<string, string> { ["quality"] = quality }));

        Assert.Equal(EvidenceQuality.Unknown, item.Quality);
    }

    [Fact]
    public void ToEvidenceItem_SerializesCompanyHintsAndMetadataIntoMetadataJson()
    {
        var mapper = CreateMapper();
        var metadata = new Dictionary<string, string>
        {
            ["sourceFile"] = "nwr.json",
            ["quality"] = "High",
        };

        var item = mapper.ToEvidenceItem(
            Build(metadata: metadata, companyHints: ["NWR", "Northwind"]));

        Assert.NotNull(item.MetadataJson);
        using var document = JsonDocument.Parse(item.MetadataJson!);
        var root = document.RootElement;

        var hints = root.GetProperty("companyHints").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(new[] { "NWR", "Northwind" }, hints);

        var metadataElement = root.GetProperty("metadata");
        Assert.Equal("nwr.json", metadataElement.GetProperty("sourceFile").GetString());
        Assert.Equal("High", metadataElement.GetProperty("quality").GetString());
    }

    [Fact]
    public void ToEvidenceItem_CarriesTimestamps_ConvertingPublishedToUtc()
    {
        var mapper = CreateMapper();
        var collectedAt = new DateTimeOffset(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);
        var publishedAt = new DateTimeOffset(2026, 2, 6, 9, 0, 0, TimeSpan.FromHours(5));

        var item = mapper.ToEvidenceItem(Build(publishedAt: publishedAt, collectedAt: collectedAt));

        Assert.Equal(collectedAt, item.CollectedAtUtc);
        Assert.Equal(publishedAt.ToUniversalTime(), item.PublishedAtUtc);
    }
}
