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
/// attention-decomposition pair at <c>{root}/live/attention-decomposition-{asOfInstant}-{runId}.md|.json</c>
/// and the NAMED failed artifact at <c>{root}/live/attention-decomposition-{asOfInstant}-{runId}-FAILED.md</c>,
/// both named by <see cref="NewsTypingArtifactNames"/> (<c>yyyyMMdd'T'HHmmss'Z'</c> instant + <c>D</c>-format
/// run id). The identity was widened from the as-of DATE by spec 208: under the date-keyed
/// <c>attention-decomposition-{yyyy-MM-dd}</c> name two full runs on one UTC date wrote the same path, and on
/// 2026-09-01 the 21:46Z scheduled run overwrote the 02:50Z run's artifact — run 3 of the spec-200 §5
/// capacity verdict, whose typing accounting then survived only in the wrapper log. One PAIR per run, never
/// one per day. Accrued date-keyed files are never renamed, migrated or rewritten (heal forward only). An
/// absent run id (not reachable today — typing runs only in unfiltered full mode, which always mints one)
/// falls back to the instant-only name with ONE Warning; no GUID is ever fabricated. Every write degrades
/// gracefully — a disk hiccup never aborts (or rolls back) the already-durable Radar run.
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
        DateTimeOffset asOfUtc,
        Guid? runId,
        string markdown,
        NewsTypingDecompositionDocument document,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(document);

        var baseName = NewsTypingArtifactNames.BaseName(asOfUtc, runId);
        WarnIfRunIdAbsent(runId, asOfUtc, "decomposition", baseName);
        var basePath = Path.Combine(_options.RootDirectory, "live", baseName);
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

    public async Task WriteFailedAsync(DateTimeOffset asOfUtc, Guid? runId, string reason, CancellationToken ct)
    {
        var failedBaseName = NewsTypingArtifactNames.FailedBaseName(asOfUtc, runId);
        WarnIfRunIdAbsent(runId, asOfUtc, "FAILED", failedBaseName);
        var path = Path.Combine(_options.RootDirectory, "live", failedBaseName + ".md");
        var runLabel = runId is { } id ? $"run {id:D}" : "run id ABSENT";
        var content =
            $"# News-typing pass FAILED — {asOfUtc:o} ({runLabel})\n\n"
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

    /// <summary>
    /// Spec 208: the absent-run-id fallback is COUNTED (one Warning per write) and the write still lands under
    /// the instant-only name. The Warning names the base name THIS write actually lands under (the FAILED
    /// path's carries the <c>-FAILED</c> suffix), so an operator locating the file is not sent to a sibling
    /// name. Never throws, never fabricates a GUID that no run record carries.
    /// </summary>
    private void WarnIfRunIdAbsent(Guid? runId, DateTimeOffset asOfUtc, string artifact, string writtenBaseName)
    {
        if (runId is null)
        {
            _logger.LogWarning(
                "News-typing {Artifact} artifact for as-of {AsOfUtc:o} has NO run id: writing the instant-only "
                    + "name {BaseName} (a same-instant run without an id would share it).",
                artifact,
                asOfUtc,
                writtenBaseName);
        }
    }
}
