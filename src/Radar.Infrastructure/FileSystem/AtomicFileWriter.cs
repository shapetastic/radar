using System.Text;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.FileSystem;

/// <summary>How an <see cref="AtomicFileWriter"/> write ended.</summary>
internal enum AtomicWriteOutcome
{
    /// <summary>The content is durably at the target path, committed by the rename.</summary>
    Committed,

    /// <summary>Nothing was written: a file already occupied the target path (insert-if-new only).</summary>
    AlreadyExists,

    /// <summary>The write degraded gracefully; nothing reached the target path and no partial file remains.</summary>
    Failed,
}

/// <summary>
/// SPEC 216 §4 — the shared temp-file + rename writer for the file stores whose records must never be
/// observable half-written. Serializing straight to the final path makes a partial file indistinguishable
/// from a complete one, and (the measured spec-216 defect) an <see cref="IOException"/> raised after the
/// file was created reads as "a concurrent writer got there first" — so a fragment becomes the record
/// forever.
/// <para>
/// The content is written to <c>{guid}.tmp</c> in the SAME directory (so the commit is a rename, never a
/// cross-volume copy), flushed, and then moved onto the target. The MOVE is the commit point: before it
/// nothing exists at the target, after it the complete content does. On any failure the temp file is
/// deleted and the caller is told nothing was persisted.
/// </para>
/// <para>
/// It is deliberately NOT a replacement for <see cref="GracefulFileWriter"/>, which spec 201 owns: that
/// helper serves the mirror stores whose last-write-wins semantics make a rewrite harmless. This one is
/// for insert-only records. Both degrade gracefully rather than throwing; only caller cancellation
/// propagates.
/// </para>
/// </summary>
internal static class AtomicFileWriter
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> ONLY if nothing is there.</summary>
    public static Task<AtomicWriteOutcome> WriteNewAsync(
        string path, string content, ILogger logger, CancellationToken ct) =>
        WriteAsync(path, content, logger, overwrite: false, ct);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>, replacing any existing file.</summary>
    public static Task<AtomicWriteOutcome> ReplaceAsync(
        string path, string content, ILogger logger, CancellationToken ct) =>
        WriteAsync(path, content, logger, overwrite: true, ct);

    private static async Task<AtomicWriteOutcome> WriteAsync(
        string path, string content, ILogger logger, bool overwrite, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(logger);

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            logger.LogWarning("Atomic write target '{Path}' has no directory; skipping.", path);
            return AtomicWriteOutcome.Failed;
        }

        if (!overwrite && File.Exists(path))
        {
            return AtomicWriteOutcome.AlreadyExists;
        }

        // The temp name deliberately does NOT extend the target name: a `*.json` enumeration on the read
        // side must never see it, whatever a platform does with compound extensions and short names.
        var tempPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);

            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(tempPath, streamOptions))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite);
            return AtomicWriteOutcome.Committed;
        }
        catch (IOException) when (!overwrite && File.Exists(path))
        {
            // An insert race: a concurrent writer committed the same path first. Whether that file is a
            // usable record is the CALLER's decision (spec 216 §4) — this helper only reports the race.
            return AtomicWriteOutcome.AlreadyExists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to write '{Path}' atomically; nothing was persisted.", path);
            return AtomicWriteOutcome.Failed;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is inert (never enumerated, never read) but it is still something
                // that did not clean up, so it is named rather than swallowed.
                logger.LogDebug(ex, "Failed to delete the temp file '{Path}'.", tempPath);
            }
        }
    }
}
