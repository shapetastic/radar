using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileEfficacyArtifactStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileEfficacyArtifactStoreTests()
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

    private FileEfficacyArtifactStore CreateStore(string? rootDirectory = null) =>
        new(
            new FileEfficacyArtifactStoreOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileEfficacyArtifactStore>.Instance);

    [Fact]
    public async Task WriteAsync_WritesSvgAndCsv_TickerLowercasedAndSanitized()
    {
        var store = CreateStore();

        // Mixed-case ticker → lowercased on disk (shares the price store's ticker key).
        var paths = await store.WriteAsync("MRCY", "<svg></svg>", "h1,h2\n", CancellationToken.None);

        var expectedSvg = Path.Combine(_tempDir, "mrcy.svg");
        var expectedCsv = Path.Combine(_tempDir, "mrcy.csv");
        Assert.Equal(expectedSvg, paths.SvgPath);
        Assert.Equal(expectedCsv, paths.CsvPath);
        Assert.True(File.Exists(expectedSvg), $"Expected SVG at {expectedSvg}.");
        Assert.True(File.Exists(expectedCsv), $"Expected CSV at {expectedCsv}.");

        Assert.Equal("<svg></svg>", await File.ReadAllTextAsync(expectedSvg));
        Assert.Equal("h1,h2\n", await File.ReadAllTextAsync(expectedCsv));
    }

    [Fact]
    public async Task WriteAsync_BlankTicker_ReturnsPlaceholderUnderRoot_NeverWritesOutsideRoot()
    {
        var store = CreateStore();

        var paths = await store.WriteAsync("   ", "<svg></svg>", "h\n", CancellationToken.None);

        // No real file was written; the returned paths stay under the root.
        Assert.StartsWith(_tempDir, paths.SvgPath, StringComparison.Ordinal);
        Assert.StartsWith(_tempDir, paths.CsvPath, StringComparison.Ordinal);
        Assert.False(File.Exists(paths.SvgPath));
        Assert.False(File.Exists(paths.CsvPath));
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathsWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws IOException on write.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var store = CreateStore(rootAsFile);

        var paths = await store.WriteAsync("MRCY", "<svg></svg>", "h\n", CancellationToken.None);

        // Attempted paths are returned (no throw); nothing crashes the run.
        Assert.Equal(Path.Combine(rootAsFile, "mrcy.svg"), paths.SvgPath);
        Assert.Equal(Path.Combine(rootAsFile, "mrcy.csv"), paths.CsvPath);
    }
}
