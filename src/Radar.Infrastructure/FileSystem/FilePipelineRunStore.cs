using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Pipeline;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk append-only run log for the pipeline (AD-8). Writes one JSON file per completed run to
/// <c>{RootDirectory}/{yyyy}/{MM}/run-{createdAt:yyyyMMddTHHmmssfffZ}-{id}.json</c>, grouping by
/// year/month so run history is trivial to browse as runs accumulate. Each run carries a fresh
/// <see cref="PipelineRunRecord.Id"/>, so files never collide and prior runs are never overwritten — this
/// is an append-only log, not an upsert-by-id mirror. All file I/O and JSON stay confined to
/// Infrastructure (AD-5); the Application sees only <see cref="IPipelineRunStore"/>. Disk failures degrade
/// gracefully (warn + return the attempted path on write; warn + skip on read) and never crash the run.
/// </summary>
public sealed class FilePipelineRunStore : IPipelineRunStore
{
    private readonly FilePipelineRunStoreOptions _options;
    private readonly ILogger<FilePipelineRunStore> _logger;

    public FilePipelineRunStore(
        FilePipelineRunStoreOptions options,
        ILogger<FilePipelineRunStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<string> WriteAsync(PipelineRunRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Format the file-name timestamp against the UTC instant so the literal 'Z' in the format string
        // is truthful, and use the invariant culture so the path is culture-independent.
        var createdAtUtc = record.CreatedAtUtc.UtcDateTime;
        var path = Path.Combine(
            _options.RootDirectory,
            createdAtUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            createdAtUtc.ToString("MM", CultureInfo.InvariantCulture),
            $"run-{createdAtUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)}-{record.Id}.json");

        var json = JsonSerializer.Serialize(record, RadarFileStoreJson.Options);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote pipeline run record {RunId} to {Path}.", record.Id, path);
        }

        return path;
    }

    public async Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return Array.Empty<PipelineRunRecord>();
        }

        if (!Directory.Exists(_options.RootDirectory))
        {
            return Array.Empty<PipelineRunRecord>();
        }

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(_options.RootDirectory, "*.json", SearchOption.AllDirectories)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to enumerate run-log files in '{RootDirectory}'; returning no history.",
                _options.RootDirectory);
            return Array.Empty<PipelineRunRecord>();
        }

        var records = new List<PipelineRunRecord>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<PipelineRunRecord>(text, RadarFileStoreJson.Options);
                if (record is not null)
                {
                    records.Add(record);
                }
                else
                {
                    // A JSON literal `null` deserializes to a null record — treat it as a malformed
                    // entry (same as a JsonException) so operators can spot corrupted run files.
                    _logger.LogWarning("Run-log file '{File}' contained a null run record; skipping.", file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // One unreadable/malformed run file must not break the whole history read.
                _logger.LogWarning(ex, "Failed to read run-log file '{File}'; skipping.", file);
            }
        }

        return records
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(count)
            .ToList();
    }
}
