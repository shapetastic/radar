using Microsoft.Extensions.Logging.Abstractions;

using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileDailyNewsReportWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "radar-tests", $"daily-news-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileDailyNewsReportWriter Writer(string root) => new(
        new FileReportWriterOptions { RootDirectory = root },
        NullLogger<FileDailyNewsReportWriter>.Instance);

    [Fact]
    public async Task WritesDatedFileUnderDaily_AndReportsWritten()
    {
        var writer = Writer(_root);

        var result = await writer.WriteAsync(
            new DateTimeOffset(2026, 9, 2, 21, 46, 0, TimeSpan.Zero), "# Radar Daily News\n", CancellationToken.None);

        Assert.True(result.Written);
        var expected = Path.Combine(_root, "daily", "radar-daily-news-2026-09-02.md");
        Assert.Equal(expected, result.Path);
        Assert.Equal("# Radar Daily News\n", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task SameDayRerunOverwrites_AReportIsADerivedView()
    {
        var writer = Writer(_root);
        var at = new DateTimeOffset(2026, 9, 2, 21, 46, 0, TimeSpan.Zero);

        await writer.WriteAsync(at, "first", CancellationToken.None);
        var second = await writer.WriteAsync(at, "second", CancellationToken.None);

        Assert.True(second.Written);
        Assert.Equal("second", await File.ReadAllTextAsync(second.Path));
    }

    [Fact]
    public async Task DiskFailure_DegradesToFailedOutcome_WithoutThrowing()
    {
        // A ROOT that is an existing FILE makes directory creation fail: the graceful writer must report
        // Failed with the attempted path rather than throwing.
        Directory.CreateDirectory(_root);
        var fileAsRoot = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(fileAsRoot, "occupied");
        var writer = Writer(fileAsRoot);

        var result = await writer.WriteAsync(
            new DateTimeOffset(2026, 9, 2, 21, 46, 0, TimeSpan.Zero), "content", CancellationToken.None);

        Assert.False(result.Written);
        Assert.Contains("radar-daily-news-2026-09-02.md", result.Path, StringComparison.Ordinal);
    }
}
