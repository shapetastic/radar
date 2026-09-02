using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Application.Reporting;
using Radar.Application.Storage;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Writes the daily news view markdown to
/// <c>{RootDirectory}/daily/radar-daily-news-{yyyy-MM-dd}.md</c>, mirroring <see cref="FileReportWriter"/>
/// in every rule that matters: a report is a derived view so a same-day overwrite is allowed (AD-1 governs
/// evidence only), all file I/O stays in Infrastructure, and a disk failure degrades gracefully (warn +
/// <see cref="DurableWriteOutcome.Failed"/> with the attempted path) and never crashes the run.
/// </summary>
public sealed class FileDailyNewsReportWriter : IDailyNewsReportWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FileReportWriterOptions _options;
    private readonly ILogger<FileDailyNewsReportWriter> _logger;

    public FileDailyNewsReportWriter(
        FileReportWriterOptions options,
        ILogger<FileDailyNewsReportWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<DurableWriteResult> WriteAsync(
        DateTimeOffset generatedAtUtc, string markdown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var path = Path.Combine(
            _options.RootDirectory,
            "daily",
            $"radar-daily-news-{generatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.md");

        var written = await GracefulFileWriter
            .TryWriteAllTextAsync(path, markdown, _logger, ct, Utf8NoBom)
            .ConfigureAwait(false);
        if (written)
        {
            _logger.LogInformation("Wrote daily news report to {Path}.", path);
        }

        return DurableWriteResult.From(path, written);
    }
}
