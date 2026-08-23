using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk store for the per-company price-efficacy artifacts (AD-14 read side): writes
/// <c>{RootDirectory}/{ticker}.svg</c> and <c>{RootDirectory}/{ticker}.csv</c> via the shared
/// <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> (reuse over copy — no second write helper). The ticker
/// is sanitized through the shared <see cref="FileTickerKey"/> — the SAME key the price file uses — so the
/// efficacy artifact and the price file line up on disk. All file I/O stays in Infrastructure (AD-5).
/// <para>
/// Best-effort (AD-8): a disk failure logs a warning and the attempted path(s) are still returned — the write
/// never throws. A blank/invalid ticker has no safe filename, so a path-shaped placeholder under the root is
/// returned (never a write outside the root).
/// </para>
/// </summary>
public sealed class FileEfficacyArtifactStore : IEfficacyArtifactStore
{
    private readonly FileEfficacyArtifactStoreOptions _options;
    private readonly ILogger<FileEfficacyArtifactStore> _logger;

    public FileEfficacyArtifactStore(
        FileEfficacyArtifactStoreOptions options,
        ILogger<FileEfficacyArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<EfficacyArtifactPaths> WriteAsync(
        string ticker, string svg, string csv, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(svg);
        ArgumentNullException.ThrowIfNull(csv);

        var sanitized = FileTickerKey.Sanitize(ticker);
        if (sanitized is null)
        {
            _logger.LogWarning(
                "Efficacy ticker '{Ticker}' is blank or contains invalid filename characters; skipping write.",
                ticker);
            return new EfficacyArtifactPaths(
                Path.Combine(_options.RootDirectory, "(invalid-ticker).svg"),
                Path.Combine(_options.RootDirectory, "(invalid-ticker).csv"));
        }

        var svgPath = Path.Combine(_options.RootDirectory, sanitized + ".svg");
        var csvPath = Path.Combine(_options.RootDirectory, sanitized + ".csv");

        if (await GracefulFileWriter.TryWriteAllTextAsync(svgPath, svg, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote efficacy SVG for '{Ticker}' to {Path}.", ticker, svgPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(csvPath, csv, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote efficacy CSV for '{Ticker}' to {Path}.", ticker, csvPath);
        }

        return new EfficacyArtifactPaths(svgPath, csvPath);
    }

    /// <summary>
    /// Writes the single strategy-vs-price leaderboard pair (spec 140) to
    /// <c>{RootDirectory}/strategy-leaderboard.{csv,md}</c> through the SAME shared
    /// <see cref="GracefulFileWriter.TryWriteAllTextAsync"/> the per-company write uses — no second write
    /// helper, and the same best-effort AD-8 posture. The file name is a fixed constant (the leaderboard is
    /// per-run, not per-ticker), so no sanitisation is needed and the write can never leave the root.
    /// </summary>
    public async Task<StrategyLeaderboardPaths> WriteLeaderboardAsync(
        string csv, string markdown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(markdown);

        var csvPath = Path.Combine(_options.RootDirectory, LeaderboardFileStem + ".csv");
        var markdownPath = Path.Combine(_options.RootDirectory, LeaderboardFileStem + ".md");

        // Spec 183 §3: the pre-excess RAW artifacts are preserved under their own semantic-version names
        // BEFORE the first excess-schema write can overwrite them, so the raw series survives as a distinct,
        // marked artifact rather than being silently replaced. Idempotent: once the raw-v1 file exists it is
        // never touched again (the current file is by then the excess series). Best-effort like every other
        // write here (AD-8).
        await PreserveRawLeaderboardAsync(
                markdownPath,
                RawMarkdownPreservationHeader,
                ".md",
                // An artifact that already names the excess basis is NOT the raw series — copying it to the
                // raw-v1 name would mislabel excess numbers as raw, the exact confusion this preservation
                // exists to prevent.
                static existing => existing.Contains("excess-vs-universe-v1", StringComparison.Ordinal),
                ct)
            .ConfigureAwait(false);
        await PreserveRawLeaderboardAsync(
                csvPath,
                prependedMarker: null,
                ".csv",
                static existing => existing.StartsWith("schemaVersion,", StringComparison.Ordinal),
                ct)
            .ConfigureAwait(false);

        if (await GracefulFileWriter.TryWriteAllTextAsync(csvPath, csv, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote strategy-comparison leaderboard CSV to {Path}.", csvPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(markdownPath, markdown, _logger, ct)
            .ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote strategy-comparison leaderboard markdown to {Path}.", markdownPath);
        }

        return new StrategyLeaderboardPaths(csvPath, markdownPath);
    }

    /// <summary>
    /// Writes the spec-155 paired-comparison pair to
    /// <c>{RootDirectory}/strategy-paired-comparison.{csv,md}</c> plus the spec-170 per-block rows to
    /// <c>{RootDirectory}/strategy-paired-comparison-blocks.csv</c> — the same fixed-name, best-effort (AD-8)
    /// posture as <see cref="WriteLeaderboardAsync"/>, through the SAME shared
    /// <see cref="GracefulFileWriter.TryWriteAllTextAsync"/>. Separate files from the leaderboard on
    /// purpose: the leaderboard is descriptive, these are the claim-bearing artifacts.
    /// </summary>
    public async Task<PairedComparisonPaths> WritePairedComparisonAsync(
        string csv, string markdown, string blocksCsv, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(blocksCsv);

        var csvPath = Path.Combine(_options.RootDirectory, PairedComparisonFileStem + ".csv");
        var markdownPath = Path.Combine(_options.RootDirectory, PairedComparisonFileStem + ".md");
        var blocksCsvPath = Path.Combine(_options.RootDirectory, PairedComparisonBlocksFileStem + ".csv");

        if (await GracefulFileWriter.TryWriteAllTextAsync(csvPath, csv, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote paired strategy-comparison CSV to {Path}.", csvPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(markdownPath, markdown, _logger, ct)
            .ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote paired strategy-comparison markdown to {Path}.", markdownPath);
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(blocksCsvPath, blocksCsv, _logger, ct)
            .ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote paired strategy-comparison blocks CSV to {Path}.", blocksCsvPath);
        }

        return new PairedComparisonPaths(csvPath, markdownPath, blocksCsvPath);
    }

    /// <summary>
    /// Copies an existing pre-183 leaderboard artifact to its <c>strategy-leaderboard-raw-v1</c> name, once.
    /// Nothing happens when there is no existing file (a fresh deployment has no raw series to preserve) or
    /// when the raw-v1 file already exists (the preservation already ran — by then the live file holds the
    /// excess series and must NOT be re-copied over the raw one). The markdown copy is prepended with a
    /// marker naming what it is and why it is not comparable with the excess series; the CSV is preserved
    /// byte-for-byte (a prepended comment would corrupt the format — its renamed file IS the marking, and
    /// its rows still carry the pre-183 header naming raw semantics).
    /// </summary>
    private async Task PreserveRawLeaderboardAsync(
        string currentPath,
        string? prependedMarker,
        string extension,
        Func<string, bool> isAlreadyExcessSchema,
        CancellationToken ct)
    {
        try
        {
            var rawPath = Path.Combine(_options.RootDirectory, RawLeaderboardFileStem + extension);
            if (!File.Exists(currentPath) || File.Exists(rawPath))
            {
                return;
            }

            var existing = await File.ReadAllTextAsync(currentPath, ct).ConfigureAwait(false);
            if (isAlreadyExcessSchema(existing))
            {
                return;
            }

            var preserved = prependedMarker is null ? existing : prependedMarker + existing;
            if (await GracefulFileWriter.TryWriteAllTextAsync(rawPath, preserved, _logger, ct)
                .ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Preserved the pre-excess raw leaderboard artifact as {Path} (spec 183: the raw and "
                        + "excess series are distinct semantic versions and are not comparable).",
                    rawPath);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort (AD-8): preservation must never block the new artifact's write.
            _logger.LogWarning(
                ex, "Could not preserve the pre-excess raw leaderboard artifact from {Path}.", currentPath);
        }
    }

    /// <summary>The spec-183 marker prepended to the preserved raw markdown artifact.</summary>
    internal const string RawMarkdownPreservationHeader =
        "> **PRESERVED RAW-RETURN SERIES (semantic v1, superseded by spec 183).** This artifact ranked by "
            + "RAW forward returns. The live strategy-leaderboard.md now ranks by EXCESS returns "
            + "(excess-vs-universe-v1) and the two series are NOT comparable — different outcomes, one "
            + "file lineage. Kept verbatim below, for the record.\n\n";

    /// <summary>The fixed leaderboard file stem (deliberately not a shape any real ticker takes).</summary>
    private const string LeaderboardFileStem = "strategy-leaderboard";

    /// <summary>The preserved pre-183 raw-return leaderboard stem (spec 183 §3).</summary>
    private const string RawLeaderboardFileStem = "strategy-leaderboard-raw-v1";

    /// <summary>The fixed paired-comparison file stem (same rule as <see cref="LeaderboardFileStem"/>).</summary>
    private const string PairedComparisonFileStem = "strategy-paired-comparison";

    /// <summary>The fixed per-block file stem (spec 170; same rule as <see cref="LeaderboardFileStem"/>).</summary>
    private const string PairedComparisonBlocksFileStem = "strategy-paired-comparison-blocks";
}
