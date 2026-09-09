using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy.FilingReads;
using Radar.Application.Storage;

namespace Radar.Infrastructure.FileSystem;

/// <summary>Root directory for the spec-218 directional-filing-read measurement artifacts.</summary>
public sealed class FileDirectionalFilingReadArtifactStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// On-disk store for the spec-218 directional-filing-read measurement: writes
/// <c>{RootDirectory}/directional-filing-reads.{json,csv,md}</c> through the shared
/// <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> — no second write helper. All file I/O stays in
/// Infrastructure (AD-5); the Application renders the strings and never touches the disk.
/// <para>
/// Best-effort (AD-8): a disk failure logs a warning and the attempted paths are still returned — the write
/// never throws. It writes ONLY these three artifacts; it can affect no score, signal, evidence or review.
/// The file stem is a fixed constant (the measurement is per-run, not per-ticker), so no sanitisation is
/// needed and the write can never leave the root.
/// </para>
/// </summary>
public sealed class FileDirectionalFilingReadArtifactStore : IDirectionalFilingReadArtifactStore
{
    /// <summary>The fixed artifact file stem.</summary>
    private const string FileStem = "directional-filing-reads";

    private readonly FileDirectionalFilingReadArtifactStoreOptions _options;
    private readonly ILogger<FileDirectionalFilingReadArtifactStore> _logger;

    public FileDirectionalFilingReadArtifactStore(
        FileDirectionalFilingReadArtifactStoreOptions options,
        ILogger<FileDirectionalFilingReadArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<DirectionalFilingReadArtifactPaths> WriteAsync(
        string json, string csv, string markdown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(markdown);

        var jsonPath = Path.Combine(_options.RootDirectory, FileStem + ".json");
        var csvPath = Path.Combine(_options.RootDirectory, FileStem + ".csv");
        var markdownPath = Path.Combine(_options.RootDirectory, FileStem + ".md");

        var jsonWritten = await GracefulFileWriter
            .TryWriteAllTextAsync(jsonPath, json, _logger, ct).ConfigureAwait(false);
        if (jsonWritten)
        {
            _logger.LogInformation("Wrote directional filing-read measurement JSON to {Path}.", jsonPath);
        }

        var csvWritten = await GracefulFileWriter
            .TryWriteAllTextAsync(csvPath, csv, _logger, ct).ConfigureAwait(false);
        if (csvWritten)
        {
            _logger.LogInformation("Wrote directional filing-read measurement CSV to {Path}.", csvPath);
        }

        var markdownWritten = await GracefulFileWriter
            .TryWriteAllTextAsync(markdownPath, markdown, _logger, ct).ConfigureAwait(false);
        if (markdownWritten)
        {
            _logger.LogInformation(
                "Wrote directional filing-read measurement markdown to {Path}.", markdownPath);
        }

        // Spec 201 §1: each file's outcome rides beside its attempted path — never a path as proof.
        return new DirectionalFilingReadArtifactPaths(
            DurableWriteResult.From(jsonPath, jsonWritten),
            DurableWriteResult.From(csvPath, csvWritten),
            DurableWriteResult.From(markdownPath, markdownWritten));
    }
}
