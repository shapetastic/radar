using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy.Attention;

namespace Radar.Infrastructure.FileSystem;

/// <summary>Root directory for the AD-16 attention-arrival screen artifacts (spec 169).</summary>
public sealed class FileAttentionArrivalArtifactStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// On-disk store for the AD-16 attention-arrival screen artifacts (spec 169): writes
/// <c>{RootDirectory}/attention-arrival-screen.{json,csv,md}</c> through the shared
/// <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> — no second write helper. All file I/O stays in
/// Infrastructure (AD-5); the Application renders the strings and never touches the disk.
/// <para>
/// Best-effort (AD-8): a disk failure logs a warning and the attempted paths are still returned — the write
/// never throws. It writes ONLY these three artifacts; it can affect no score, signal, evidence or review.
/// The file stem is a fixed constant (the screen is per-run, not per-ticker), so no sanitisation is needed and
/// the write can never leave the root.
/// </para>
/// </summary>
public sealed class FileAttentionArrivalArtifactStore : IAttentionArrivalArtifactStore
{
    /// <summary>The fixed artifact file stem.</summary>
    private const string FileStem = "attention-arrival-screen";

    private readonly FileAttentionArrivalArtifactStoreOptions _options;
    private readonly ILogger<FileAttentionArrivalArtifactStore> _logger;

    public FileAttentionArrivalArtifactStore(
        FileAttentionArrivalArtifactStoreOptions options,
        ILogger<FileAttentionArrivalArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<AttentionArrivalArtifactPaths> WriteAsync(
        string json, string csv, string markdown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(markdown);

        var jsonPath = Path.Combine(_options.RootDirectory, FileStem + ".json");
        var csvPath = Path.Combine(_options.RootDirectory, FileStem + ".csv");
        var markdownPath = Path.Combine(_options.RootDirectory, FileStem + ".md");

        if (await GracefulFileWriter.TryWriteAllTextAsync(jsonPath, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote attention-arrival screen JSON to {Path}.", jsonPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(csvPath, csv, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote attention-arrival screen CSV to {Path}.", csvPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(markdownPath, markdown, _logger, ct)
            .ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote attention-arrival screen markdown to {Path}.", markdownPath);
        }

        return new AttentionArrivalArtifactPaths(jsonPath, csvPath, markdownPath);
    }
}
