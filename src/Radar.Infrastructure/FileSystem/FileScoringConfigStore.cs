using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Scoring;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Content-addressed on-disk store of the effective resolved scoring config (spec 91). Writes one JSON file
/// per distinct config to <c>{RootDirectory}/{config.Fingerprint}.json</c> (filename = the
/// <c>ScoringConfigVersion</c> fingerprint), serializing with <see cref="RadarFileStoreJson.Options"/> via
/// <see cref="GracefulFileWriter"/>. This completes the spec-89 provenance chain: a snapshot's
/// <c>ScoringConfigVersion</c> stamp dereferences back to the exact weights that produced it. All file I/O
/// and JSON stay confined to Infrastructure (AD-5); the Application sees only <see cref="IScoringConfigStore"/>.
/// Disk failures degrade gracefully (warn + return the attempted path) and never crash the run — the
/// snapshot still carries its fingerprint.
/// </summary>
/// <remarks>
/// <b>Insert-if-new (immutable, AD-1 mirror).</b> A given fingerprint's config is by definition fixed — the
/// same content always hashes to the same filename — so an existing <c>{fingerprint}.json</c> is NEVER
/// overwritten: if the file exists the write is skipped and the existing path returned. This is deliberate
/// and the DIRECT OPPOSITE of <see cref="FileScoreSnapshotStore"/>'s upsert-by-Id (last-write-wins): a
/// snapshot's id is a fresh Guid each run so re-writing is meaningful, whereas a config is content-addressed
/// and immutable so re-writing could only ever re-produce identical bytes. Mirrors the AD-1 evidence
/// immutability semantics. The benign check-then-write race is acceptable for the MVP single-process runner:
/// two concurrent writers would write identical bytes anyway (content-addressed), so no locking is added.
/// </remarks>
public sealed class FileScoringConfigStore : IScoringConfigStore
{
    private readonly FileScoringConfigStoreOptions _options;
    private readonly ILogger<FileScoringConfigStore> _logger;

    public FileScoringConfigStore(
        FileScoringConfigStoreOptions options,
        ILogger<FileScoringConfigStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<string> WriteIfNewAsync(EffectiveScoringConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        // The fingerprint is a filename-safe lowercase-hex-with-prefix token (spec 89) — no path separators.
        var path = Path.Combine(_options.RootDirectory, config.Fingerprint + ".json");

        // Insert-if-new (immutable): a given fingerprint's config is fixed, so an existing file already
        // holds identical content — skip the write, never overwrite (the AD-1 evidence-immutability mirror).
        if (File.Exists(path))
        {
            _logger.LogDebug(
                "Effective scoring config {Fingerprint} already exists at {Path}; skipping (immutable).",
                config.Fingerprint,
                path);
            return path;
        }

        var json = JsonSerializer.Serialize(config, RadarFileStoreJson.Options);

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Wrote effective scoring config {Fingerprint} to {Path}.", config.Fingerprint, path);
        }

        return path;
    }
}
