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
        EvidenceSourceType sourceType = EvidenceSourceType.LocalFile,
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
    [InlineData(EvidenceSourceType.LocalFile)]
    [InlineData(EvidenceSourceType.PressRelease)]
    [InlineData(EvidenceSourceType.RssFeed)]
    [InlineData(EvidenceSourceType.NewsArticle)]
    public void ToEvidenceItem_CarriesDeclaredSourceTypeThroughUnchanged(EvidenceSourceType declared)
    {
        var mapper = CreateMapper();

        var item = mapper.ToEvidenceItem(Build(sourceType: declared));

        Assert.Equal(declared, item.SourceType);
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
    public void ToEvidenceItem_RssPressReleaseBaselineQuality_MapsToMediumNotUnknown()
    {
        var mapper = CreateMapper();

        // Mirrors the metadata the RSS press-release collector emits: a declared "Medium" baseline.
        // Proves a first-party press release maps to EvidenceQuality.Medium, not Unknown.
        var item = mapper.ToEvidenceItem(
            Build(
                sourceType: EvidenceSourceType.PressRelease,
                metadata: new Dictionary<string, string> { ["quality"] = "Medium" }));

        Assert.Equal(EvidenceQuality.Medium, item.Quality);
        Assert.NotEqual(EvidenceQuality.Unknown, item.Quality);
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
    [InlineData("bogus")]
    public void ToEvidenceItem_BlankDigitOnlyOrUnparseableQuality_DefaultsToUnknown(string quality)
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

    // ---------------------------------------------------------------------------------------------
    // Content-derived, stable evidence identity (spec 145).
    //
    // Pre-145 the mapper minted Guid.NewGuid() per call, so these assertions were all false: the id a
    // signal referenced was unrelated to the contentHash-keyed id the durable store persisted (only 10.5%
    // of accrued signals resolved), and the spec-85 dedupe key — which contains EvidenceId — could never
    // collapse content duplication (measured 1.000x, versus 9.213x by content).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ToEvidenceItem_IdIsDerivedFromTheContentHash()
    {
        var mapper = CreateMapper();

        var item = mapper.ToEvidenceItem(Build());

        Assert.Equal(EvidenceIdentity.ForContentHash(item.ContentHash), item.Id);
    }

    [Fact]
    public void ToEvidenceItem_TwoRunsOverIdenticalContent_ProduceTheSameId()
    {
        var mapper = CreateMapper();

        var first = mapper.ToEvidenceItem(Build());
        var second = mapper.ToEvidenceItem(Build());

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ToEvidenceItem_ExcludedFieldsDoNotChangeIdentity()
    {
        // Identity is a function of the normalized title+body ALONE. Everything varied here is a property
        // of the RETRIEVAL, not of the fact: the collector/source name, the URL (hence every volatile query
        // parameter and tracking token), the collection timestamp, the published timestamp, the declared
        // source type, the metadata bag and the company hints. Folding any of them in would re-create the
        // per-run identity this replaces.
        var mapper = CreateMapper();

        var baseline = mapper.ToEvidenceItem(Build());

        var varied = mapper.ToEvidenceItem(Build(
            sourceType: EvidenceSourceType.NewsArticle,
            sourceName: "Some Other Wire",
            sourceUrl: "https://other.example/x?utm_source=newsletter&sid=9f3a1",
            publishedAt: new DateTimeOffset(2026, 2, 7, 3, 0, 0, TimeSpan.Zero),
            collectedAt: new DateTimeOffset(2026, 3, 9, 1, 2, 3, TimeSpan.Zero),
            metadata: new Dictionary<string, string> { ["quality"] = "Low", ["run"] = "17" },
            companyHints: ["NWR"]));

        Assert.Equal(baseline.Id, varied.Id);
        Assert.Equal(baseline.ContentHash, varied.ContentHash);

        // …while everything else genuinely differs, so the equality above is about identity, not about the
        // two items accidentally being the same item.
        Assert.NotEqual(baseline.SourceName, varied.SourceName);
        Assert.NotEqual(baseline.SourceType, varied.SourceType);
        Assert.NotEqual(baseline.CollectedAtUtc, varied.CollectedAtUtc);
    }

    [Fact]
    public void ToEvidenceItem_DifferentContent_ProducesDifferentIds()
    {
        // The other half of the contract: identity collapses copies, it must never merge distinct facts.
        var mapper = CreateMapper();

        var a = mapper.ToEvidenceItem(Build(title: "Northwind Robotics customer win"));
        var b = mapper.ToEvidenceItem(Build(title: "Northwind Robotics guidance cut"));
        var c = mapper.ToEvidenceItem(Build(rawText: "Northwind Robotics lowered guidance today."));

        Assert.Equal(3, new[] { a.Id, b.Id, c.Id }.Distinct().Count());
    }

    [Fact]
    public void ToEvidenceItem_CarriesTimestamps_ConvertingPublishedToUtc()
    {
        var mapper = CreateMapper();
        var collectedAt = new DateTimeOffset(2026, 2, 8, 12, 0, 0, TimeSpan.FromHours(2));
        var publishedAt = new DateTimeOffset(2026, 2, 6, 9, 0, 0, TimeSpan.FromHours(5));

        var item = mapper.ToEvidenceItem(Build(publishedAt: publishedAt, collectedAt: collectedAt));

        Assert.Equal(collectedAt.ToUniversalTime(), item.CollectedAtUtc);
        Assert.Equal(TimeSpan.Zero, item.CollectedAtUtc.Offset);
        Assert.Equal(publishedAt.ToUniversalTime(), item.PublishedAtUtc);
    }
}
