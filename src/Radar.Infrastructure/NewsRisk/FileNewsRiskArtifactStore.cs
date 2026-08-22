using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsRisk;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsRisk;

/// <summary>Options for <see cref="FileNewsRiskArtifactStore"/>: the news-risk output root (default <c>data/news-risk</c>).</summary>
public sealed class FileNewsRiskArtifactStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// The news-risk artifact writer (spec 179 §7/§9) over the shared <see cref="GracefulFileWriter"/>: live
/// artifacts at <c>{root}/live/news-risk-{asOfDate}.md|.json</c>, the NAMED failed artifact at
/// <c>{root}/live/news-risk-{asOfDate}-FAILED.md</c>, and the evaluator pair at
/// <c>{root}/evaluation/news-risk-evaluation.md|.csv</c>. Every write degrades gracefully — a disk hiccup
/// never aborts (or rolls back) the already-durable Radar run.
/// </summary>
public sealed class FileNewsRiskArtifactStore : INewsRiskArtifactStore
{
    private readonly FileNewsRiskArtifactStoreOptions _options;
    private readonly ILogger<FileNewsRiskArtifactStore> _logger;

    public FileNewsRiskArtifactStore(
        FileNewsRiskArtifactStoreOptions options, ILogger<FileNewsRiskArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task WriteLiveAsync(
        string asOfDateToken, string markdown, NewsRiskLiveDocument document, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOfDateToken);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(document);

        var basePath = Path.Combine(_options.RootDirectory, "live", $"news-risk-{asOfDateToken}");
        await GracefulFileWriter
            .TryWriteAllTextAsync(basePath + ".md", markdown, _logger, ct)
            .ConfigureAwait(false);
        await GracefulFileWriter
            .TryWriteAllTextAsync(
                basePath + ".json",
                JsonSerializer.Serialize(document, RadarFileStoreJson.Options),
                _logger,
                ct)
            .ConfigureAwait(false);
        _logger.LogInformation("News-risk live artifact written: {Path}.md/.json", basePath);
    }

    public async Task WriteFailedAsync(string asOfDateToken, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOfDateToken);

        var path = Path.Combine(
            _options.RootDirectory, "live", $"news-risk-{asOfDateToken}-FAILED.md");
        var content =
            $"# News-risk shadow read FAILED — {asOfDateToken}\n\n"
                + "The shadow step failed and assessed nothing. The Radar run itself is unaffected — "
                + "no score, label, rank or report was rolled back or relabelled.\n\n"
                + $"Reason: {reason}\n";
        await GracefulFileWriter.TryWriteAllTextAsync(path, content, _logger, ct).ConfigureAwait(false);
        _logger.LogWarning("News-risk FAILED artifact written: {Path} ({Reason})", path, reason);
    }

    public async Task WriteEvaluationAsync(string markdown, string csv, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(csv);

        var basePath = Path.Combine(_options.RootDirectory, "evaluation", "news-risk-evaluation");
        await GracefulFileWriter
            .TryWriteAllTextAsync(basePath + ".md", markdown, _logger, ct)
            .ConfigureAwait(false);
        await GracefulFileWriter
            .TryWriteAllTextAsync(basePath + ".csv", csv, _logger, ct)
            .ConfigureAwait(false);
        _logger.LogInformation("News-risk evaluation artifact written: {Path}.md/.csv", basePath);
    }
}
