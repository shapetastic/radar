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

    [Fact]
    public async Task WriteLeaderboardAsync_WritesOneFixedNamedPairUnderTheRoot()
    {
        var store = CreateStore();

        var paths = await store.WriteLeaderboardAsync(
            "status,rank\nranked,1\n", "# Strategy vs price\n", CancellationToken.None);

        var expectedCsv = Path.Combine(_tempDir, "strategy-leaderboard.csv");
        var expectedMd = Path.Combine(_tempDir, "strategy-leaderboard.md");
        Assert.Equal(expectedCsv, paths.CsvPath);
        Assert.Equal(expectedMd, paths.MarkdownPath);
        Assert.Equal("status,rank\nranked,1\n", await File.ReadAllTextAsync(expectedCsv));
        Assert.Equal("# Strategy vs price\n", await File.ReadAllTextAsync(expectedMd));
    }

    [Fact]
    public async Task WriteLeaderboardAsync_OverwritesInPlace_AndCoexistsWithPerCompanyArtifacts()
    {
        var store = CreateStore();

        await store.WriteAsync("MRCY", "<svg></svg>", "h\n", CancellationToken.None);
        await store.WriteLeaderboardAsync("first\n", "# first\n", CancellationToken.None);
        var paths = await store.WriteLeaderboardAsync("second\n", "# second\n", CancellationToken.None);

        // Idempotent: a re-run replaces its own output rather than accumulating a second copy.
        Assert.Equal("second\n", await File.ReadAllTextAsync(paths.CsvPath));
        Assert.Equal("# second\n", await File.ReadAllTextAsync(paths.MarkdownPath));

        // The existing per-company artifacts are untouched.
        Assert.True(File.Exists(Path.Combine(_tempDir, "mrcy.svg")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "mrcy.csv")));
    }

    [Fact]
    public async Task WriteLeaderboardAsync_IoFailure_ReturnsAttemptedPathsWithoutThrowing()
    {
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir-either");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var store = CreateStore(rootAsFile);

        var paths = await store.WriteLeaderboardAsync("csv\n", "md\n", CancellationToken.None);

        Assert.Equal(Path.Combine(rootAsFile, "strategy-leaderboard.csv"), paths.CsvPath);
        Assert.Equal(Path.Combine(rootAsFile, "strategy-leaderboard.md"), paths.MarkdownPath);
    }

    [Fact]
    public async Task WritePairedComparisonAsync_WritesTheFixedNamedTrioUnderTheRoot()
    {
        var store = CreateStore();

        var paths = await store.WritePairedComparisonAsync(
            "status,baseline\nbaseline,x\n",
            "# Paired comparison\n",
            "baseline,blockDate\nbaseline-x,2026-01-01\n",
            CancellationToken.None);

        var expectedCsv = Path.Combine(_tempDir, "strategy-paired-comparison.csv");
        var expectedMd = Path.Combine(_tempDir, "strategy-paired-comparison.md");
        var expectedBlocks = Path.Combine(_tempDir, "strategy-paired-comparison-blocks.csv");
        Assert.Equal(expectedCsv, paths.CsvPath);
        Assert.Equal(expectedMd, paths.MarkdownPath);
        Assert.Equal(expectedBlocks, paths.BlocksCsvPath);
        Assert.Equal("status,baseline\nbaseline,x\n", await File.ReadAllTextAsync(expectedCsv));
        Assert.Equal("# Paired comparison\n", await File.ReadAllTextAsync(expectedMd));
        Assert.Equal(
            "baseline,blockDate\nbaseline-x,2026-01-01\n", await File.ReadAllTextAsync(expectedBlocks));
    }

    [Fact]
    public async Task WritePairedComparisonAsync_OverwritesInPlace_AndCoexistsWithTheLeaderboardPair()
    {
        var store = CreateStore();

        await store.WriteLeaderboardAsync("leader\n", "# leader\n", CancellationToken.None);
        await store.WritePairedComparisonAsync("first\n", "# first\n", "blocks1\n", CancellationToken.None);
        var paths = await store.WritePairedComparisonAsync(
            "second\n", "# second\n", "blocks2\n", CancellationToken.None);

        Assert.Equal("second\n", await File.ReadAllTextAsync(paths.CsvPath));
        Assert.Equal("# second\n", await File.ReadAllTextAsync(paths.MarkdownPath));
        Assert.Equal("blocks2\n", await File.ReadAllTextAsync(paths.BlocksCsvPath));

        // The DESCRIPTIVE leaderboard and the claim-bearing paired artifact are separate files by design.
        Assert.Equal("leader\n", await File.ReadAllTextAsync(Path.Combine(_tempDir, "strategy-leaderboard.csv")));
    }

    [Fact]
    public async Task WritePairedComparisonAsync_IoFailure_ReturnsAttemptedPathsWithoutThrowing()
    {
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir-paired");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var store = CreateStore(rootAsFile);

        var paths = await store.WritePairedComparisonAsync(
            "csv\n", "md\n", "blocks\n", CancellationToken.None);

        Assert.Equal(Path.Combine(rootAsFile, "strategy-paired-comparison.csv"), paths.CsvPath);
        Assert.Equal(Path.Combine(rootAsFile, "strategy-paired-comparison.md"), paths.MarkdownPath);
        Assert.Equal(
            Path.Combine(rootAsFile, "strategy-paired-comparison-blocks.csv"), paths.BlocksCsvPath);
    }
}
