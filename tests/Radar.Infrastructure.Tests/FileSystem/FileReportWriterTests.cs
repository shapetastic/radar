using Microsoft.Extensions.Logging.Abstractions;

using Radar.Domain.Reports;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.Tests.FileSystem;

public sealed class FileReportWriterTests : IDisposable
{
    private readonly string _tempDir;

    public FileReportWriterTests()
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

    private FileReportWriter CreateWriter(string? rootDirectory = null) =>
        new(
            new FileReportWriterOptions { RootDirectory = rootDirectory ?? _tempDir },
            NullLogger<FileReportWriter>.Instance);

    private static RadarReport BuildReport(
        DateTimeOffset periodEndUtc, string markdown) =>
        new(
            Id: Guid.NewGuid(),
            ReportType: "Weekly",
            Title: "Radar Weekly",
            PeriodStartUtc: periodEndUtc.AddDays(-7),
            PeriodEndUtc: periodEndUtc,
            MarkdownContent: markdown,
            CreatedAtUtc: periodEndUtc);

    [Fact]
    public async Task WriteAsync_WritesWeeklyReportAtExpectedPathWithMarkdown()
    {
        var periodEnd = new DateTimeOffset(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);
        const string markdown = "# Radar Weekly\n\n- Northwind Robotics: Investigate\n";
        var report = BuildReport(periodEnd, markdown);

        var writer = CreateWriter();
        var path = await writer.WriteAsync(report, CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "weekly", "radar-weekly-2026-02-08.md");
        Assert.Equal(expectedPath, path);
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}.");

        var content = await File.ReadAllTextAsync(expectedPath);
        Assert.Equal(markdown, content);
    }

    [Fact]
    public async Task WriteAsync_SamePeriod_OverwritesExistingReport()
    {
        var periodEnd = new DateTimeOffset(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);
        var writer = CreateWriter();

        await writer.WriteAsync(BuildReport(periodEnd, "# First\n"), CancellationToken.None);

        const string secondMarkdown = "# Second\n\n- Updated\n";
        await writer.WriteAsync(BuildReport(periodEnd, secondMarkdown), CancellationToken.None);

        var expectedPath = Path.Combine(_tempDir, "weekly", "radar-weekly-2026-02-08.md");
        var content = await File.ReadAllTextAsync(expectedPath);
        Assert.Equal(secondMarkdown, content);
    }

    [Fact]
    public async Task WriteAsync_IoFailure_ReturnsAttemptedPathWithoutThrowing()
    {
        // Point the root at an existing FILE so Directory.CreateDirectory throws.
        var rootAsFile = Path.Combine(_tempDir, "not-a-dir");
        await File.WriteAllTextAsync(rootAsFile, "x");

        var periodEnd = new DateTimeOffset(2026, 2, 8, 12, 0, 0, TimeSpan.Zero);
        var report = BuildReport(periodEnd, "# Radar Weekly\n");

        var writer = CreateWriter(rootAsFile);

        var path = await writer.WriteAsync(report, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(path));
    }
}
