using System.Text;

using Microsoft.Extensions.Logging;
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

    // ---------------------------------------------------------------------------------------------
    // Spec 195 §1 — the failure-log MODE. The catch set and the graceful `false` are unchanged; only who
    // reports the failure moves, and only where the caller owns a proven aggregate.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The DEFAULT is <see cref="GracefulFileWriteFailureLogging.Immediate"/> and is byte-for-byte the
    /// pre-195 behaviour: one Warning, carrying the exception, naming the attempted path. Every caller that
    /// does not opt in keeps this — spec 195 §1 must not silently suppress failures anywhere else.
    /// </summary>
    [Fact]
    public async Task TryWriteAllTextAsync_DefaultMode_LogsOneWarningCarryingTheException()
    {
        var logger = new CapturingLogger();
        var path = await BlockedPathAsync();

        var wrote = await GracefulFileWriter.TryWriteAllTextAsync(path, "content", logger, CancellationToken.None);

        Assert.False(wrote);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(path, warning.Message, StringComparison.Ordinal);
        Assert.NotNull(warning.Exception);
    }

    /// <summary>
    /// Passing <see cref="GracefulFileWriteFailureLogging.Immediate"/> explicitly is the same thing as
    /// omitting it — pinned so the default can never drift away from the named mode.
    /// </summary>
    [Fact]
    public async Task TryWriteAllTextAsync_ExplicitImmediate_MatchesTheDefault()
    {
        var logger = new CapturingLogger();

        var wrote = await GracefulFileWriter.TryWriteAllTextAsync(
            await BlockedPathAsync(),
            "content",
            logger,
            CancellationToken.None,
            encoding: null,
            failureLogging: GracefulFileWriteFailureLogging.Immediate);

        Assert.False(wrote);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// <see cref="GracefulFileWriteFailureLogging.CallerAggregates"/> suppresses ONLY the Warning. The
    /// attempted path survives at Debug in bounded form — no exception, therefore no stack trace, therefore
    /// no N-stack-trace flood at Warning level when a whole batch fails.
    /// </summary>
    [Fact]
    public async Task TryWriteAllTextAsync_CallerAggregates_LogsNoWarning_ButKeepsThePathAtDebug()
    {
        var logger = new CapturingLogger();
        var path = await BlockedPathAsync();

        var wrote = await GracefulFileWriter.TryWriteAllTextAsync(
            path,
            "content",
            logger,
            CancellationToken.None,
            encoding: null,
            failureLogging: GracefulFileWriteFailureLogging.CallerAggregates);

        // The graceful return is unchanged: the caller still learns the write did not happen.
        Assert.False(wrote);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);

        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains(path, debug.Message, StringComparison.Ordinal);
        Assert.Null(debug.Exception);
    }

    /// <summary>A path under an existing FILE, so <c>Directory.CreateDirectory</c> throws <see cref="IOException"/>.</summary>
    private async Task<string> BlockedPathAsync()
    {
        var blockingFile = Path.Combine(_tempDir, Path.GetRandomFileName());
        await File.WriteAllTextAsync(blockingFile, "x");
        return Path.Combine(blockingFile, "child", "file.txt");
    }

    private static bool StartsWithUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
