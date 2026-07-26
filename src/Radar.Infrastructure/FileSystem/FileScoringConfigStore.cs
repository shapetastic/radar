using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Scoring;
using Radar.Application.Storage;

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
/// <para>
/// Alongside the content-addressed files it keeps a per-STRATEGY-NAME record at
/// <c>{RootDirectory}/strategies/{name}.json</c> holding <c>{ strategyName, fingerprint }</c> (spec 141).
/// Unlike the config files this one is MUTABLE (last-write-wins upsert): it answers "what did this NAME
/// resolve to last time", which is exactly the question the startup tripwire asks. Keeping it in a
/// <c>strategies/</c> subdirectory means it can never collide with a <c>{fingerprint}.json</c> file, so the
/// existing content-addressed shape is untouched.
/// </para>
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
    /// <summary>
    /// Subdirectory holding the per-strategy-name fingerprint records. A subdirectory (rather than a
    /// filename prefix) keeps the root's content-addressed <c>{fingerprint}.json</c> listing exactly as it
    /// was — a fingerprint token can never be a directory name, so the two namespaces cannot collide.
    /// </summary>
    private const string StrategiesFolder = "strategies";

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

        string json;
        try
        {
            json = JsonSerializer.Serialize(config, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            // Serialization must not crash the run either (matches GracefulFileWriter's disk-failure posture):
            // the snapshot still carries its fingerprint; only the dereferenceable config file is skipped.
            _logger.LogWarning(
                ex,
                "Failed to serialize effective scoring config {Fingerprint}; skipping write to {Path}.",
                config.Fingerprint,
                path);
            return path;
        }

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Wrote effective scoring config {Fingerprint} to {Path}.", config.Fingerprint, path);
        }

        return path;
    }

    public async Task<string?> ReadStrategyFingerprintAsync(string strategyName, CancellationToken ct)
    {
        var path = StrategyRecordPath(strategyName);

        if (!File.Exists(path))
        {
            // Never recorded: a brand-new strategy name. The guard records it and continues.
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<StrategyFingerprintFile>(text, RadarFileStoreJson.Options);
            var recorded = parsed?.Fingerprint;
            if (string.IsNullOrWhiteSpace(recorded))
            {
                _logger.LogWarning(
                    "Strategy fingerprint record '{Path}' carries no fingerprint; treating as unrecorded.", path);
                return null;
            }

            return recorded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Graceful degrade (AD-8): "cannot read" must read as "unrecorded", never as "changed" — a disk
            // hiccup must not fail a run through the tripwire. OperationCanceledException still propagates.
            _logger.LogWarning(
                ex, "Failed to read strategy fingerprint record '{Path}'; treating as unrecorded.", path);
            return null;
        }
    }

    public async Task<string> RecordStrategyFingerprintAsync(
        string strategyName, string fingerprint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var path = StrategyRecordPath(strategyName);
        var record = new StrategyFingerprintFile(strategyName, fingerprint);

        string json;
        try
        {
            json = JsonSerializer.Serialize(record, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(
                ex, "Failed to serialize strategy fingerprint record for {StrategyName}; skipping write to {Path}.",
                strategyName, path);
            return path;
        }

        // Upsert (last-write-wins), the deliberate opposite of the insert-if-new config files above: this
        // record tracks what a NAME resolves to NOW, so a legitimate re-record must overwrite.
        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Recorded strategy {StrategyName} fingerprint {Fingerprint} at {Path}.",
                strategyName, fingerprint, path);
        }

        return path;
    }

    /// <summary>
    /// <c>{RootDirectory}/strategies/{name}.json</c>. The name is validated as a single storage segment
    /// (<see cref="StorageSegmentName"/> — the same rule <c>ScoringStrategySet</c> enforces at startup) so an
    /// operator-supplied name can never escape the store root.
    /// </summary>
    private string StrategyRecordPath(string strategyName)
    {
        if (!StorageSegmentName.IsUsable(strategyName))
        {
            throw new ArgumentException(
                $"Strategy name '{strategyName}' is used verbatim as a storage file name, so "
                    + $"{StorageSegmentName.Rule}.",
                nameof(strategyName));
        }

        return Path.Combine(_options.RootDirectory, StrategiesFolder, strategyName + ".json");
    }

    /// <summary>The per-name record's persisted shape (camelCase via the shared serializer options).</summary>
    private sealed record StrategyFingerprintFile(string StrategyName, string Fingerprint);
}
