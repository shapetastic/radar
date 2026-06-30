using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class GracefulFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public GracefulFileWriterTests()
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

    [Fact]
    public async Task TryWriteAllTextAsync_FreshPath_CreatesDirectoriesWritesContentAndReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "nested", "deeper", "file.txt");
        const string content = "hello radar\nsecond line\n";

        var wrote = await GracefulFileWriter.TryWriteAllTextAsync(
            path, content, NullLogger.Instance, CancellationToken.None);

        Assert.True(wrote);
        Assert.True(File.Exists(path), $"Expected file at {path}.");

        var roundTripped = await File.ReadAllTextAsync(path);
        Assert.Equal(content, roundTripped);
    }

    [Fact]
    public async Task TryWriteAllTextAsync_IoFailure_ReturnsFalseWithoutThrowingAndCreatesNoFile()
    {
        // Point the path under an existing FILE so Directory.CreateDirectory throws IOException.
        var blockingFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(blockingFile, "x");

        var path = Path.Combine(blockingFile, "child", "file.txt");

        var wrote = await GracefulFileWriter.TryWriteAllTextAsync(
            path, "content", NullLogger.Instance, CancellationToken.None);

        Assert.False(wrote);
        Assert.False(File.Exists(path), "No file should be created when the write fails.");
    }

    [Fact]
    public async Task TryWriteAllTextAsync_Utf8NoBomEncoding_DoesNotEmitBom()
    {
        var path = Path.Combine(_tempDir, "no-bom.txt");

        var wrote = await GracefulFileWriter.TryWriteAllTextAsync(
            path, "content", NullLogger.Instance, CancellationToken.None, new UTF8Encoding(false));

        Assert.True(wrote);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(StartsWithUtf8Bom(bytes), "UTF-8 no-BOM encoding must not emit a BOM.");
    }

    [Fact]
    public async Task TryWriteAllTextAsync_NullEncoding_DoesNotEmitBom()
    {
        var path = Path.Combine(_tempDir, "default-no-bom.txt");

        var wrote = await GracefulFileWriter.TryWriteAllTextAsync(
            path, "content", NullLogger.Instance, CancellationToken.None);

        Assert.True(wrote);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(StartsWithUtf8Bom(bytes), "Default (null) encoding must not emit a BOM.");
    }

    private static bool StartsWithUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
