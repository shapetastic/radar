using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Evidence;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Persistence.InMemory;
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
            new EvidenceNormalizer(),
            new LocalFileEvidenceCollectorOptions { SourceDirectory = directory ?? _tempDir },
            NullLogger<LocalFileEvidenceCollector>.Instance,
            new FixedTimeProvider(FixedNow));

    private void WriteFile(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_tempDir, fileName), content);

    private static string ValidDocJson(string title, string rawText, string? sourceName = null) =>
        $$"""
        {
          "sourceName": {{(sourceName is null ? "null" : $"\"{sourceName}\"")}},
          "sourceUrl": "https://example.com/x",
          "title": "{{title}}",
          "summary": "A summary",
          "publishedAtUtc": "2026-06-01T13:00:00Z",
          "rawText": "{{rawText}}"
        }
        """;

    [Fact]
    public async Task CollectAsync_TwoValidFiles_ProducesTwoEvidenceItems()
    {
        WriteFile("a.json", ValidDocJson("Title A", "Body A"));
        WriteFile("b.json", ValidDocJson("Title B", "Body B"));

        var items = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(EvidenceSourceType.LocalFile, item.SourceType);
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.RawText));
            Assert.False(string.IsNullOrWhiteSpace(item.ContentHash));
            Assert.Equal(EvidenceQuality.Unknown, item.Quality);
            Assert.Equal(FixedNow, item.CollectedAtUtc);
            Assert.NotNull(item.MetadataJson);
        });
    }

    [Fact]
    public async Task CollectAsync_OrdersByFileNameDeterministically()
    {
        WriteFile("b.json", ValidDocJson("Title B", "Body B"));
        WriteFile("a.json", ValidDocJson("Title A", "Body A"));

        var items = await CreateCollector().CollectAsync(CancellationToken.None);

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

        var items = await CreateCollector().CollectAsync(CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Good", item.Title);
    }

    [Fact]
    public async Task CollectAsync_MissingDirectory_ReturnsEmptyAndDoesNotThrow()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");

        var items = await CreateCollector(missing).CollectAsync(CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task CollectAsync_SourceNameFallsBackToFileName_WhenAbsent()
    {
        WriteFile("acme-q3.json", ValidDocJson("Title", "Body", sourceName: null));

        var items = await CreateCollector().CollectAsync(CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("acme-q3", item.SourceName);
    }

    [Fact]
    public async Task CollectAsync_ContentHash_MatchesNormalizerAndDedupesInRepository()
    {
        const string title = "Roundtrip Title";
        const string rawText = "Roundtrip body text";
        WriteFile("rt.json", ValidDocJson(title, rawText));

        var items = await CreateCollector().CollectAsync(CancellationToken.None);
        var item = Assert.Single(items);

        var expectedHash = new EvidenceNormalizer().Normalize(title, rawText).ContentHash;
        Assert.Equal(expectedHash, item.ContentHash);

        var repository = new InMemoryEvidenceRepository();
        Assert.True(await repository.AddIfNewAsync(item, CancellationToken.None));
        Assert.False(await repository.AddIfNewAsync(item, CancellationToken.None));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
