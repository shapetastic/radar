using System.Text;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Shared write helper for the file-store mirrors. Ensures the target directory exists, writes the text
/// content, and degrades gracefully on disk failure (logs a warning and returns <c>false</c> instead of
/// throwing) so a disk hiccup never crashes the pipeline run — the in-memory repository copy still
/// exists. Callers own path construction and any success logging.
/// </summary>
internal static class GracefulFileWriter
{
    /// <returns><c>true</c> if the file was written; <c>false</c> if the write degraded gracefully.</returns>
    public static async Task<bool> TryWriteAllTextAsync(
        string path,
        string content,
        ILogger logger,
        CancellationToken ct,
        Encoding? encoding = null)
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
            // A disk hiccup must not crash the run; the in-memory copy still exists.
            logger.LogWarning(ex, "Failed to write file to {Path}; skipping.", path);
            return false;
        }
    }
}
