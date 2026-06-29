using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Evidence;
using Radar.Infrastructure.FileSystem;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileRawEvidenceStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileRawEvidenceStoreTests()
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

    private FileRawEvidenceStore CreateStore(string? rootDirectory = null) =>
        new(
            new FileRawEvidenceStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileRawEvidenceStore>.Instance);

    [Fact]
    public async Task WriteIfNewAsync_NewEvidence_WritesFileAtExpectedPathAndRoundTrips()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithSourceName("Rocket Lab Investor News")
            .WithSourceUrl("https://example.com/rklb")
            .WithTitle("Rocket Lab Announces New Multi-Launch Agreement")
            .WithRawText("Rocket Lab signed a multi-launch agreement.")
            .WithContentHash("6AF3E9")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 1, 10, 8, 0, 0, TimeSpan.Zero))
            .WithCollectedAtUtc(new DateTimeOffset(2026, 1, 10, 10, 15, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{"k":"v"},"companyHints":["RKLB"]}""")
            .Build();

        var store = CreateStore();
        var wrote = await store.WriteIfNewAsync(evidence, CancellationToken.None);

        Assert.True(wrote);

        var expectedPath = Path.Combine(_tempDir, "press-releases", "2026", "01", "6AF3E9.json");
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");

        await using var stream = File.OpenRead(expectedPath);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        Assert.Equal(evidence.Id.ToString(), root.GetProperty("evidenceId").GetString());
        Assert.Equal("press_release", root.GetProperty("sourceType").GetString());
        Assert.Equal("Rocket Lab Investor News", root.GetProperty("sourceName").GetString());
        Assert.Equal("https://example.com/rklb", root.GetProperty("sourceUrl").GetString());
        Assert.Equal("Rocket Lab Announces New Multi-Launch Agreement", root.GetProperty("title").GetString());
        Assert.Equal("Rocket Lab signed a multi-launch agreement.", root.GetProperty("rawText").GetString());
        Assert.Equal("6AF3E9", root.GetProperty("contentHash").GetString());

        var hints = root.GetProperty("companyHints").EnumerateArray().Select(h => h.GetString()!).ToArray();
        Assert.Equal(["RKLB"], hints);
        Assert.Equal("v", root.GetProperty("metadata").GetProperty("k").GetString());

        // normalizedText is intentionally absent.
        Assert.False(root.TryGetProperty("normalizedText", out _));
    }

    [Fact]
    public async Task WriteIfNewAsync_CalledTwice_IsInsertOnlyAndLeavesFileUnchanged()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("HASH123")
            .WithPublishedAtUtc(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var store = CreateStore();

        var first = await store.WriteIfNewAsync(evidence, CancellationToken.None);
        Assert.True(first);

        var path = Path.Combine(_tempDir, "press-releases", "2026", "03", "HASH123.json");
        var bytesBefore = await File.ReadAllBytesAsync(path);

        var second = await store.WriteIfNewAsync(evidence, CancellationToken.None);
        Assert.False(second);

        var bytesAfter = await File.ReadAllBytesAsync(path);
        Assert.Equal(bytesBefore, bytesAfter);
    }

    [Fact]
    public async Task WriteIfNewAsync_DerivesYearMonthFromPublishedAt_WhenPresent()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("PUB")
            .WithPublishedAtUtc(new DateTimeOffset(2025, 11, 20, 9, 0, 0, TimeSpan.Zero))
            .WithCollectedAtUtc(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        await CreateStore().WriteIfNewAsync(evidence, CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "press-releases", "2025", "11", "PUB.json");
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");
    }

    [Fact]
    public async Task WriteIfNewAsync_DerivesYearMonthFromCollectedAt_WhenPublishedAtAbsent()
    {
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("COL")
            .WithPublishedAtUtc(null)
            .WithCollectedAtUtc(new DateTimeOffset(2024, 7, 9, 0, 0, 0, TimeSpan.Zero))
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        await CreateStore().WriteIfNewAsync(evidence, CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "press-releases", "2024", "07", "COL.json");
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");
    }

    [Fact]
    public async Task WriteIfNewAsync_IoFailure_ReturnsFalseWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.PressRelease)
            .WithContentHash("IOFAIL")
            .WithMetadataJson("""{"metadata":{},"companyHints":[]}""")
            .Build();

        var store = CreateStore(rootAsFile);

        var wrote = await store.WriteIfNewAsync(evidence, CancellationToken.None);
        Assert.False(wrote);
    }
}
