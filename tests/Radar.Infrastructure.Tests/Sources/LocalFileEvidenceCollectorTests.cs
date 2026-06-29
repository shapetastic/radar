using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

public sealed class LocalFileEvidenceCollectorTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public LocalFileEvidenceCollectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    private LocalFileEvidenceCollector CreateCollector(string? directory = null) =>
        new(
            new LocalFileEvidenceCollectorOptions { SourceDirectory = directory ?? _tempDir },
            NullLogger<LocalFileEvidenceCollector>.Instance,
            new FixedTimeProvider(FixedNow));

    private static Task<CollectionResult> CollectAsync(
        LocalFileEvidenceCollector collector) =>
        collector.CollectAsync(new CollectionContext([]), CancellationToken.None);

    private static async Task<IReadOnlyCollection<CollectedEvidence>> CollectEvidenceAsync(
        LocalFileEvidenceCollector collector) =>
        (await CollectAsync(collector)).Evidence;

    private void WriteFile(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_tempDir, fileName), content);

    private static string ValidDocJson(
        string title,
        string rawText,
        string? sourceName = null,
        string? quality = null) =>
        $$"""
        {
          "sourceName": {{(sourceName is null ? "null" : $"\"{sourceName}\"")}},
          "sourceUrl": "https://example.com/x",
          "title": "{{title}}",
          "summary": "A summary",
          "publishedAtUtc": "2026-06-01T13:00:00Z",
          "rawText": "{{rawText}}"{{(quality is null ? string.Empty : $",\n          \"quality\": \"{quality}\"")}}
        }
        """;

    [Fact]
    public async Task CollectAsync_TwoValidFiles_ProducesTwoCollectedEvidence()
    {
        WriteFile("a.json", ValidDocJson("Title A", "Body A"));
        WriteFile("b.json", ValidDocJson("Title B", "Body B"));

        var items = await CollectEvidenceAsync(CreateCollector());

        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(EvidenceSourceType.LocalFile, item.SourceType);
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.RawText));
            Assert.Equal(FixedNow, item.CollectedAt);
            Assert.True(item.Metadata.ContainsKey("sourceFile"));
        });
    }

    [Fact]
    public async Task CollectAsync_OrdersByFileNameDeterministically()
    {
        WriteFile("b.json", ValidDocJson("Title B", "Body B"));
        WriteFile("a.json", ValidDocJson("Title A", "Body A"));

        var items = (await CollectAsync(CreateCollector())).Evidence.ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("Title A", items[0].Title);
        Assert.Equal("Title B", items[1].Title);
    }

    [Fact]
    public async Task CollectAsync_SkipsMalformedAndIncompleteFiles()
    {
        WriteFile("valid.json", ValidDocJson("Good", "Good body"));
        WriteFile("malformed.json", "{ this is not valid json ");
        WriteFile("no-title.json", """{ "rawText": "has body but no title" }""");
        WriteFile("no-rawtext.json", """{ "title": "has title but no body" }""");

        var items = await CollectEvidenceAsync(CreateCollector());

        var item = Assert.Single(items);
        Assert.Equal("Good", item.Title);
    }

    [Fact]
    public async Task CollectAsync_MissingDirectory_ReturnsEmptyAndDoesNotThrow()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");

        var items = await CollectEvidenceAsync(CreateCollector(missing));

        Assert.Empty(items);
    }

    [Fact]
    public async Task CollectAsync_OneGoodOneMalformed_SummaryCountsAndNamesFailure()
    {
        WriteFile("good.json", ValidDocJson("Good", "Good body"));
        WriteFile("bad.json", "{ this is not valid json ");

        var result = await CollectAsync(CreateCollector());

        // The good file's evidence is still returned.
        var item = Assert.Single(result.Evidence);
        Assert.Equal("Good", item.Title);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);

        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("bad.json", failure.SourceName);
        Assert.Null(failure.SourceUrl);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
    }

    [Fact]
    public async Task CollectAsync_MissingDirectory_SummaryIsEmpty()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");

        var result = await CollectAsync(CreateCollector(missing));

        Assert.Empty(result.Evidence);
        Assert.Equal(CollectionSummary.Empty, result.Summary);
    }

    [Fact]
    public async Task CollectAsync_SourceNameFallsBackToFileName_WhenAbsent()
    {
        WriteFile("acme-q3.json", ValidDocJson("Title", "Body", sourceName: null));

        var items = await CollectEvidenceAsync(CreateCollector());

        var item = Assert.Single(items);
        Assert.Equal("acme-q3", item.SourceName);
    }

    [Fact]
    public async Task CollectAsync_CarriesRawTextAndSourceFileMetadata()
    {
        const string title = "Roundtrip Title";
        const string rawText = "Roundtrip body text";
        WriteFile("rt.json", ValidDocJson(title, rawText));

        var items = await CollectEvidenceAsync(CreateCollector());
        var item = Assert.Single(items);

        // Raw text is carried through unchanged — normalization/hashing now live in the mapper.
        Assert.Equal(rawText, item.RawText);
        Assert.Equal("rt.json", item.Metadata["sourceFile"]);
    }

    [Theory]
    [InlineData("PrimarySource")]
    [InlineData("High")]
    [InlineData("medium")]
    [InlineData("LOW")]
    public async Task CollectAsync_DeclaredQuality_CarriedRawInMetadata(string declared)
    {
        WriteFile("q.json", ValidDocJson("Title", "Body", quality: declared));

        var items = await CollectEvidenceAsync(CreateCollector());

        var item = Assert.Single(items);
        Assert.Equal(declared, item.Metadata["quality"]);
    }

    [Fact]
    public async Task CollectAsync_OmittedQuality_OmitsMetadataKey()
    {
        WriteFile("q.json", ValidDocJson("Title", "Body"));

        var items = await CollectEvidenceAsync(CreateCollector());

        var item = Assert.Single(items);
        Assert.False(item.Metadata.ContainsKey("quality"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
