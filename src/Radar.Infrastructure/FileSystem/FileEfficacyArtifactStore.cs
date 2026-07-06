using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk store for the per-company price-efficacy artifacts (AD-14 read side): writes
/// <c>{RootDirectory}/{ticker}.svg</c> and <c>{RootDirectory}/{ticker}.csv</c> via the shared
/// <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> (reuse over copy — no second write helper). The ticker
/// is sanitized through the shared <see cref="FileTickerKey"/> — the SAME key the price file uses — so the
/// efficacy artifact and the price file line up on disk. All file I/O stays in Infrastructure (AD-5).
/// <para>
/// Best-effort (AD-8): a disk failure logs a warning and the attempted path(s) are still returned — the write
/// never throws. A blank/invalid ticker has no safe filename, so a path-shaped placeholder under the root is
/// returned (never a write outside the root).
/// </para>
/// </summary>
public sealed class FileEfficacyArtifactStore : IEfficacyArtifactStore
{
    private readonly FileEfficacyArtifactStoreOptions _options;
    private readonly ILogger<FileEfficacyArtifactStore> _logger;

    public FileEfficacyArtifactStore(
        FileEfficacyArtifactStoreOptions options,
        ILogger<FileEfficacyArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<EfficacyArtifactPaths> WriteAsync(
        string ticker, string svg, string csv, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(svg);
        ArgumentNullException.ThrowIfNull(csv);

        var sanitized = FileTickerKey.Sanitize(ticker);
        if (sanitized is null)
        {
            _logger.LogWarning(
                "Efficacy ticker '{Ticker}' is blank or contains invalid filename characters; skipping write.",
                ticker);
            return new EfficacyArtifactPaths(
                Path.Combine(_options.RootDirectory, "(invalid-ticker).svg"),
                Path.Combine(_options.RootDirectory, "(invalid-ticker).csv"));
        }

        var svgPath = Path.Combine(_options.RootDirectory, sanitized + ".svg");
        var csvPath = Path.Combine(_options.RootDirectory, sanitized + ".csv");

        if (await GracefulFileWriter.TryWriteAllTextAsync(svgPath, svg, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote efficacy SVG for '{Ticker}' to {Path}.", ticker, svgPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(csvPath, csv, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote efficacy CSV for '{Ticker}' to {Path}.", ticker, csvPath);
        }

        return new EfficacyArtifactPaths(svgPath, csvPath);
    }
}
