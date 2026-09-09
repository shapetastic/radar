using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Storage;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

/// <summary>
/// The spec-218 artifact store: a fixed stem under the efficacy root, best-effort (AD-8), and each file's
/// outcome returned beside its attempted path — a path is never proof that the file exists (spec 201 §1).
/// </summary>
public sealed class FileDirectionalFilingReadArtifactStoreTests
{
    [Fact]
    public async Task Write_ProducesTheTripleUnderTheEfficacyRoot()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileDirectionalFilingReadArtifactStore(
                new FileDirectionalFilingReadArtifactStoreOptions { RootDirectory = dir },
                NullLogger<FileDirectionalFilingReadArtifactStore>.Instance);

            var paths = await store.WriteAsync("{}", "csv", "# md", CancellationToken.None);

            Assert.Equal(0, paths.NotPersistedCount);
            Assert.Equal(Path.Combine(dir, "directional-filing-reads.json"), paths.JsonPath);
            Assert.Equal(Path.Combine(dir, "directional-filing-reads.csv"), paths.CsvPath);
            Assert.Equal(Path.Combine(dir, "directional-filing-reads.md"), paths.MarkdownPath);
            Assert.Equal("{}", await File.ReadAllTextAsync(paths.JsonPath));
            Assert.Equal("csv", await File.ReadAllTextAsync(paths.CsvPath));
            Assert.Equal("# md", await File.ReadAllTextAsync(paths.MarkdownPath));
            Assert.All(
                new[] { paths.Json, paths.Csv, paths.Markdown },
                r => Assert.Equal(DurableWriteOutcome.Written, r.Outcome));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Write_ToAnUnusableRoot_DegradesGracefully_AndReportsEveryFileAsNotPersisted()
    {
        var dir = NewTempDir();
        try
        {
            // A FILE where the root directory should be: creating the directory must fail, and the store
            // must degrade rather than throw (AD-8) — a failed report can never damage the record.
            var blocked = Path.Combine(dir, "blocked");
            await File.WriteAllTextAsync(blocked, "not a directory");

            var store = new FileDirectionalFilingReadArtifactStore(
                new FileDirectionalFilingReadArtifactStoreOptions { RootDirectory = blocked },
                NullLogger<FileDirectionalFilingReadArtifactStore>.Instance);

            var paths = await store.WriteAsync("{}", "csv", "# md", CancellationToken.None);

            Assert.Equal(3, paths.NotPersistedCount);
            Assert.All(
                new[] { paths.Json, paths.Csv, paths.Markdown },
                r =>
                {
                    Assert.False(r.Written);
                    // The attempted path is still returned — that is what makes a failure diagnosable.
                    Assert.False(string.IsNullOrEmpty(r.Path));
                });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "radar-filingread-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
