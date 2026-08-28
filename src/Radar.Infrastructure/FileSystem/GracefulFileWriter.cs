using System.Text;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Shared write helper for the file-store mirrors. Ensures the target directory exists, writes the text
/// content, and degrades gracefully on disk failure (logs a warning and returns <c>false</c> instead of
/// throwing) so a disk hiccup never crashes the pipeline run — the in-memory repository copy still
/// exists. Callers own path construction and any success logging.
/// </summary>
/// <remarks>
/// Spec 195 §1: WHO reports the failure is now selectable via
/// <see cref="GracefulFileWriteFailureLogging"/>, because the two pipeline batch stores gained their own
/// aggregated per-pass Warning in spec 193 and were emitting both. The catch set, the graceful
/// <c>false</c> return and the default logging behaviour are unchanged.
/// </remarks>
internal static class GracefulFileWriter
{
    /// <param name="failureLogging">
    /// Who reports a graceful failure (spec 195 §1). Defaults to
    /// <see cref="GracefulFileWriteFailureLogging.Immediate"/>, which preserves every pre-spec-195 caller's
    /// behaviour exactly. Pass <see cref="GracefulFileWriteFailureLogging.CallerAggregates"/> only from a
    /// call site whose owner emits a proven aggregated Warning for the same failures.
    /// </param>
    /// <returns><c>true</c> if the file was written; <c>false</c> if the write degraded gracefully.</returns>
    public static async Task<bool> TryWriteAllTextAsync(
        string path,
        string content,
        ILogger logger,
        CancellationToken ct,
        Encoding? encoding = null,
        GracefulFileWriteFailureLogging failureLogging = GracefulFileWriteFailureLogging.Immediate)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (encoding is null)
            {
                await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(path, content, encoding, ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk hiccup must not crash the run; the in-memory copy still exists. The catch set and the
            // graceful `false` are unchanged by spec 195 — only WHO logs the failure can move.
            if (failureLogging == GracefulFileWriteFailureLogging.CallerAggregates)
            {
                // The caller owns a later aggregated Warning for exactly these failures, so a per-file
                // Warning here would duplicate it N times. The attempted path stays recoverable at Debug,
                // in bounded form: no exception, hence no stack trace, hence no N-stack-trace log flood.
                logger.LogDebug(
                    "Failed to write file to {Path}; skipping (reported by the caller's aggregated warning).",
                    path);
                return false;
            }

            logger.LogWarning(ex, "Failed to write file to {Path}; skipping.", path);
            return false;
        }
    }
}
