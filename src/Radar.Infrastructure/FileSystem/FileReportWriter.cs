using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Reporting;
using Radar.Domain.Reports;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Writes the derived weekly report markdown to
/// <c>{RootDirectory}/weekly/radar-weekly-{yyyy-MM-dd}.md</c>. Overwriting an existing report file
/// is allowed: a report is a derived view, not immutable evidence (AD-1 governs evidence only). All
/// file I/O is confined to Infrastructure; the Application sees only
/// <see cref="IReportFileWriter"/>. Disk failures degrade gracefully (warn + return the attempted
/// path) and never crash the run; the in-memory report still exists.
/// </summary>
public sealed class FileReportWriter : IReportFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FileReportWriterOptions _options;
    private readonly ILogger<FileReportWriter> _logger;

    public FileReportWriter(
        FileReportWriterOptions options,
        ILogger<FileReportWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<string> WriteAsync(RadarReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        var path = Path.Combine(
            _options.RootDirectory,
            "weekly",
            $"radar-weekly-{report.PeriodEndUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Overwrite-allowed: reports are derived views, not immutable evidence (AD-1). UTF-8 with
            // no BOM preserves the renderer's '\n' line endings byte-for-byte.
            await File.WriteAllTextAsync(path, report.MarkdownContent, Utf8NoBom, ct).ConfigureAwait(false);

            _logger.LogInformation("Wrote weekly report {ReportId} to {Path}.", report.Id, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk hiccup must not crash the run; the in-memory report still exists.
            _logger.LogWarning(
                ex,
                "Failed to write weekly report {ReportId} to {Path}; skipping.",
                report.Id,
                path);
            return path;
        }
    }
}
