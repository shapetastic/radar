using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileAttentionArrivalArtifactStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileAttentionArrivalArtifactStoreTests()
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
            // Best-effort cleanup.
        }
    }

    private FileAttentionArrivalArtifactStore CreateStore(string? root = null) =>
        new(
            new FileAttentionArrivalArtifactStoreOptions { RootDirectory = root ?? _tempDir },
            NullLogger<FileAttentionArrivalArtifactStore>.Instance);

    [Fact]
    public async Task WriteAsync_WritesTheThreeArtifactsUnderTheFixedStem()
    {
        var paths = await CreateStore().WriteAsync("{}", "a,b\n", "# md\n", CancellationToken.None);

        Assert.Equal(Path.Combine(_tempDir, "attention-arrival-screen.json"), paths.JsonPath);
        Assert.Equal(Path.Combine(_tempDir, "attention-arrival-screen.csv"), paths.CsvPath);
        Assert.Equal(Path.Combine(_tempDir, "attention-arrival-screen.md"), paths.MarkdownPath);

        Assert.Equal("{}", await File.ReadAllTextAsync(paths.JsonPath));
        Assert.Equal("a,b\n", await File.ReadAllTextAsync(paths.CsvPath));
        Assert.Equal("# md\n", await File.ReadAllTextAsync(paths.MarkdownPath));
    }

    [Fact]
    public async Task WriteAsync_ReWritingIdenticalContent_LeavesIdenticalBytes()
    {
        var store = CreateStore();
        await store.WriteAsync("{}", "a,b\n", "# md\n", CancellationToken.None);
        var first = await File.ReadAllBytesAsync(Path.Combine(_tempDir, "attention-arrival-screen.json"));

        await store.WriteAsync("{}", "a,b\n", "# md\n", CancellationToken.None);
        var second = await File.ReadAllBytesAsync(Path.Combine(_tempDir, "attention-arrival-screen.json"));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsTheAttemptedPathsWithoutThrowing()
    {
        // Point the root at an existing FILE so directory creation fails. Best-effort (AD-8): a read-side
        // report that cannot be written must never be able to damage the record it reports on.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var paths = await CreateStore(rootAsFile).WriteAsync("{}", "", "", CancellationToken.None);

        Assert.Equal(Path.Combine(rootAsFile, "attention-arrival-screen.json"), paths.JsonPath);
        Assert.False(File.Exists(paths.JsonPath));
    }
}
