using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsTyping;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsTyping;

/// <summary>Options for <see cref="FileFactFamilySnapshotStore"/>: the news-typing output root (snapshots live under <c>{root}/families/</c>).</summary>
public sealed class FileFactFamilySnapshotStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// The checkpoint family-snapshot writer (spec 181 §4), on disk as
/// <c>{root}/families/{cohort-policy-segment}/{checkpointUtc:yyyyMMdd'T'HHmmss'Z'}.json</c>. Append-only by
/// construction: every checkpoint writes its OWN timestamped file through the shared graceful writer — a
/// later run writes a new snapshot, never edits an old one, and a disk hiccup never aborts the run.
/// </summary>
public sealed class FileFactFamilySnapshotStore : IFactFamilySnapshotStore
{
    private const string FamiliesFolder = "families";

    private readonly FileFactFamilySnapshotStoreOptions _options;
    private readonly ILogger<FileFactFamilySnapshotStore> _logger;

    public FileFactFamilySnapshotStore(
        FileFactFamilySnapshotStoreOptions options, ILogger<FileFactFamilySnapshotStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<bool> WriteAsync(
        string policySegment, FactFamilySnapshot snapshot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policySegment);
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = Path.Combine(
            _options.RootDirectory,
            FamiliesFolder,
            policySegment,
            snapshot.CheckpointUtc.UtcDateTime.ToString(
                "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".json");
        var written = await GracefulFileWriter
            .TryWriteAllTextAsync(
                path, JsonSerializer.Serialize(snapshot, RadarFileStoreJson.Options), _logger, ct)
            .ConfigureAwait(false);
        if (written)
        {
            _logger.LogInformation(
                "Fact-family checkpoint written: {Path} ({Families} family(ies) over {Facts} fact(s)).",
                path,
                snapshot.Families.Count,
                snapshot.FactsConsidered);
        }

        return written;
    }
}
