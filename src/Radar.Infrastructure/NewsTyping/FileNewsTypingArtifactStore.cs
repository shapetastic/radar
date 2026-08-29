using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsTyping;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsTyping;

/// <summary>Options for <see cref="FileNewsTypingArtifactStore"/>: the news-typing output root (default <c>data/news-typing</c>).</summary>
public sealed class FileNewsTypingArtifactStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// The news-typing artifact writer (spec 181 §5) over the shared <see cref="GracefulFileWriter"/>: the
/// attention-decomposition pair at <c>{root}/live/attention-decomposition-{asOfDate}.md|.json</c> and the
/// NAMED failed artifact at <c>{root}/live/attention-decomposition-{asOfDate}-FAILED.md</c>. Every write
/// degrades gracefully — a disk hiccup never aborts (or rolls back) the already-durable Radar run.
/// </summary>
public sealed class FileNewsTypingArtifactStore : INewsTypingArtifactStore
{
    private readonly FileNewsTypingArtifactStoreOptions _options;
    private readonly ILogger<FileNewsTypingArtifactStore> _logger;

    public FileNewsTypingArtifactStore(
        FileNewsTypingArtifactStoreOptions options, ILogger<FileNewsTypingArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task WriteDecompositionAsync(
        string asOfDateToken,
        string markdown,
        NewsTypingDecompositionDocument document,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOfDateToken);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(document);

        var basePath = Path.Combine(
            _options.RootDirectory, "live", $"attention-decomposition-{asOfDateToken}");
        var markdownWritten = await GracefulFileWriter
            .TryWriteAllTextAsync(basePath + ".md", markdown, _logger, ct)
            .ConfigureAwait(false);
        var jsonWritten = await GracefulFileWriter
            .TryWriteAllTextAsync(
                basePath + ".json",
                JsonSerializer.Serialize(document, RadarFileStoreJson.Options),
                _logger,
                ct)
            .ConfigureAwait(false);
        // Spec 201 §1: the "written" line is gated on BOTH writes; a failure names the path that did not land.
        if (markdownWritten && jsonWritten)
        {
            _logger.LogInformation("Attention-decomposition artifact written: {Path}.md/.json", basePath);
        }
        else
        {
            _logger.LogWarning(
                "Attention-decomposition artifact NOT (fully) written: markdown {MarkdownWritten}, json "
                    + "{JsonWritten} at {Path}.md/.json — the write(s) degraded gracefully.",
                markdownWritten,
                jsonWritten,
                basePath);
        }
    }

    public async Task WriteFailedAsync(string asOfDateToken, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOfDateToken);

        var path = Path.Combine(
            _options.RootDirectory, "live", $"attention-decomposition-{asOfDateToken}-FAILED.md");
        var content =
            $"# News-typing pass FAILED — {asOfDateToken}\n\n"
                + "The typing step failed and typed nothing. The Radar run itself is unaffected — no score, "
                + "label, rank or report was rolled back or relabelled.\n\n"
                + $"Reason: {reason}\n";
        var written = await GracefulFileWriter.TryWriteAllTextAsync(path, content, _logger, ct).ConfigureAwait(false);
        if (written)
        {
            _logger.LogWarning("News-typing FAILED artifact written: {Path} ({Reason})", path, reason);
        }
        else
        {
            _logger.LogWarning(
                "News-typing FAILED artifact could NOT be written to {Path} ({Reason}): the write degraded gracefully.",
                path,
                reason);
        }
    }
}
