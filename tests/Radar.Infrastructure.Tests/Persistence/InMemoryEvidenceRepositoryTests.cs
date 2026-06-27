using Radar.Domain.Evidence;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Infrastructure.Tests.Persistence;

public class InMemoryEvidenceRepositoryTests
{
    private static EvidenceItem MakeItem(
        Guid id,
        string contentHash,
        string title,
        string rawText)
        => new(
            Id: id,
            SourceType: EvidenceSourceType.NewsArticle,
            SourceName: "Example Wire",
            SourceUrl: "https://example.com/article",
            Title: title,
            Summary: null,
            RawText: rawText,
            ContentHash: contentHash,
            PublishedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CollectedAtUtc: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            Quality: EvidenceQuality.Medium,
            MetadataJson: null);

    [Fact]
    public async Task AddIfNewAsync_NewItem_ReturnsTrueAndIsRetrievable()
    {
        var repo = new InMemoryEvidenceRepository();
        var id = Guid.NewGuid();
        var item = MakeItem(id, "hash-1", "First Title", "first raw text");

        var added = await repo.AddIfNewAsync(item, CancellationToken.None);

        Assert.True(added);

        var byId = await repo.GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(byId);
        Assert.Equal(id, byId!.Id);

        var byHash = await repo.GetByContentHashAsync("hash-1", CancellationToken.None);
        Assert.NotNull(byHash);
        Assert.Equal(id, byHash!.Id);
    }

    [Fact]
    public async Task AddIfNewAsync_DuplicateContentHash_ReturnsFalseAndDoesNotOverwrite()
    {
        var repo = new InMemoryEvidenceRepository();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var first = MakeItem(firstId, "dup-hash", "Original Title", "original raw text");
        var second = MakeItem(secondId, "dup-hash", "Replacement Title", "replacement raw text");

        var firstAdded = await repo.AddIfNewAsync(first, CancellationToken.None);
        var secondAdded = await repo.AddIfNewAsync(second, CancellationToken.None);

        Assert.True(firstAdded);
        Assert.False(secondAdded);

        var stored = await repo.GetByContentHashAsync("dup-hash", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(firstId, stored!.Id);
        Assert.Equal("Original Title", stored.Title);
        Assert.Equal("original raw text", stored.RawText);

        // The second item's Id was never registered.
        var bySecondId = await repo.GetByIdAsync(secondId, CancellationToken.None);
        Assert.Null(bySecondId);

        var all = await repo.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task AddIfNewAsync_DuplicateId_ReturnsFalseAndRollsBackHashIndex()
    {
        var repo = new InMemoryEvidenceRepository();
        var id = Guid.NewGuid();

        var first = MakeItem(id, "hash-a", "Original Title", "original raw text");
        // Same Id, different content hash — the Id collision must reject the add and
        // must not leave the new content hash pointing at a non-existent record.
        var second = MakeItem(id, "hash-b", "Replacement Title", "replacement raw text");

        var firstAdded = await repo.AddIfNewAsync(first, CancellationToken.None);
        var secondAdded = await repo.AddIfNewAsync(second, CancellationToken.None);

        Assert.True(firstAdded);
        Assert.False(secondAdded);

        // The original record is untouched.
        var stored = await repo.GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("Original Title", stored!.Title);

        // The rolled-back hash index has no dangling entry.
        var byNewHash = await repo.GetByContentHashAsync("hash-b", CancellationToken.None);
        Assert.Null(byNewHash);

        var all = await repo.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task GetByIdAsync_Absent_ReturnsNull()
    {
        var repo = new InMemoryEvidenceRepository();

        var result = await repo.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByContentHashAsync_Absent_ReturnsNull()
    {
        var repo = new InMemoryEvidenceRepository();

        var result = await repo.GetByContentHashAsync("missing-hash", CancellationToken.None);

        Assert.Null(result);
    }
}
