using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy.DenominatorAudit;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk store for the spec-172 audit artifact pair: writes
/// <c>{RootDirectory}/score-move-denominator.csv</c> and <c>{RootDirectory}/score-move-denominator.md</c>
/// via the shared <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> (reuse over copy — no second write
/// helper, and the directory is created only when a write actually happens, so default-off creates
/// nothing). The file names are fixed constants (the audit is per-run, not per-ticker), so no sanitisation
/// is needed and the write can never leave the root. All file I/O stays in Infrastructure (AD-5).
/// <para>
/// Best-effort (AD-8): a disk failure logs a warning and the attempted paths are still returned — the write
/// never throws.
/// </para>
/// </summary>
public sealed class FileDenominatorAuditArtifactStore : IDenominatorAuditArtifactStore
{
    /// <summary>The fixed artifact file stem (deliberately not a shape any real ticker takes).</summary>
    private const string FileStem = "score-move-denominator";

    private readonly FileDenominatorAuditArtifactStoreOptions _options;
    private readonly ILogger<FileDenominatorAuditArtifactStore> _logger;

    public FileDenominatorAuditArtifactStore(
        FileDenominatorAuditArtifactStoreOptions options,
        ILogger<FileDenominatorAuditArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<DenominatorAuditPaths> WriteAsync(string csv, string markdown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(markdown);

        var csvPath = Path.Combine(_options.RootDirectory, FileStem + ".csv");
        var markdownPath = Path.Combine(_options.RootDirectory, FileStem + ".md");

        if (await GracefulFileWriter.TryWriteAllTextAsync(csvPath, csv, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote score-move denominator audit CSV to {Path}.", csvPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(markdownPath, markdown, _logger, ct)
            .ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote score-move denominator audit markdown to {Path}.", markdownPath);
        }

        return new DenominatorAuditPaths(csvPath, markdownPath);
    }
}
