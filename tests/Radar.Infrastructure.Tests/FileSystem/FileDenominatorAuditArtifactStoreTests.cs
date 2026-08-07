using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileDenominatorAuditArtifactStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileDenominatorAuditArtifactStoreTests()
    {
        // Deliberately NOT created here: the store must not need a pre-existing directory, and construction
        // must not create one.
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
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

    private FileDenominatorAuditArtifactStore CreateStore() =>
        new(
            new FileDenominatorAuditArtifactStoreOptions { RootDirectory = _tempDir },
            NullLogger<FileDenominatorAuditArtifactStore>.Instance);

    [Fact]
    public void Construction_CreatesNoDirectory()
    {
        // Spec 172: default-off must leave no directory behind — and registration constructs the store, so
        // the directory may only appear when a write actually happens.
        _ = CreateStore();

        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public async Task WriteAsync_WritesBothArtifacts_CreatingTheDirectoryOnlyThen()
    {
        var store = CreateStore();
        Assert.False(Directory.Exists(_tempDir));

        var paths = await store.WriteAsync("csv-content", "md-content", CancellationToken.None);

        Assert.Equal(Path.Combine(_tempDir, "score-move-denominator.csv"), paths.CsvPath);
        Assert.Equal(Path.Combine(_tempDir, "score-move-denominator.md"), paths.MarkdownPath);
        Assert.Equal("csv-content", await File.ReadAllTextAsync(paths.CsvPath));
        Assert.Equal("md-content", await File.ReadAllTextAsync(paths.MarkdownPath));
    }

    [Fact]
    public async Task WriteAsync_OverwritesThePreviousRun_FixedFileNames()
    {
        var store = CreateStore();
        await store.WriteAsync("first-csv", "first-md", CancellationToken.None);

        var paths = await store.WriteAsync("second-csv", "second-md", CancellationToken.None);

        Assert.Equal("second-csv", await File.ReadAllTextAsync(paths.CsvPath));
        Assert.Equal("second-md", await File.ReadAllTextAsync(paths.MarkdownPath));
        Assert.Equal(2, Directory.GetFiles(_tempDir).Length); // still exactly one artifact pair
    }
}
