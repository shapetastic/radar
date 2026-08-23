using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsTyping;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsTyping;

/// <summary>Options for <see cref="FileNewsTypingStore"/>: the news-typing output root (typings live under <c>{root}/typings/</c>).</summary>
public sealed class FileNewsTypingStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// The insert-only durable news-typing store (spec 181 §4), on disk as
/// <c>{root}/typings/{policy-segment}/{yyyy}/{MM}/{typingId}.json</c> — the policy segment is LAYOUT only
/// (<see cref="NewsTypingCohortPath"/>); the record's <c>CohortKey</c> FIELD stays the authoritative
/// identity. Follows the <see cref="Radar.Infrastructure.News.FileNewsObservationArchive"/> mechanism: lazy
/// once-per-instance thread-safe hydration into an id index, deterministic ordinal enumeration,
/// <c>TryAdd</c>-only indexing and <see cref="FileMode.CreateNew"/> writes; a malformed file is logged and
/// skipped, never thrown.
/// </summary>
public sealed class FileNewsTypingStore : INewsTypingStore
{
    private const string TypingsFolder = "typings";

    private readonly FileNewsTypingStoreOptions _options;
    private readonly ILogger<FileNewsTypingStore> _logger;
    private readonly ConcurrentDictionary<Guid, NewsTypingRecord> _byId = new();
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    public FileNewsTypingStore(FileNewsTypingStoreOptions options, ILogger<FileNewsTypingStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<bool> WriteAsync(NewsTypingRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        if (!_byId.TryAdd(record.TypingId, record))
        {
            // Same deterministic identity ⇒ the same run/reader/observation was already durably recorded —
            // a re-run dedupe, never an overwrite.
            return true;
        }

        var partition = record.CreatedAtUtc.UtcDateTime;
        var path = Path.Combine(
            _options.RootDirectory,
            TypingsFolder,
            NewsTypingCohortPath.PolicySegment(record.Provider, record.ModelId),
            partition.ToString("yyyy", CultureInfo.InvariantCulture),
            partition.ToString("MM", CultureInfo.InvariantCulture),
            record.TypingId.ToString("D") + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using var stream = new FileStream(path, streamOptions);
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(record, RadarFileStoreJson.Options));
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex) when (File.Exists(path))
        {
            _logger.LogDebug(
                ex,
                "News-typing record {TypingId} already exists at {Path} (concurrent writer won).",
                record.TypingId,
                path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _byId.TryRemove(record.TypingId, out _);
            _logger.LogWarning(
                ex, "Failed to write news-typing record {TypingId} at {Path}.", record.TypingId, path);
            return false;
        }
    }

    public async Task<IReadOnlyList<NewsTypingRecord>> GetAllAsync(CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);
        return [.. _byId.Values.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.TypingId)];
    }

    public async Task<NewsTypingRecord?> FindCompletedAsync(
        string cohortKey, Guid observationId, string payloadHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Most recent completed typing wins; deterministic tiebreak on id (AD-3).
        return _byId.Values
            .Where(r => string.Equals(r.CohortKey, cohortKey, StringComparison.Ordinal)
                && r.ObservationId == observationId
                && string.Equals(r.PayloadHash, payloadHash, StringComparison.Ordinal)
                && r.IsCompletedTyping)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.TypingId)
            .FirstOrDefault();
    }

    private async Task EnsureHydratedAsync(CancellationToken ct)
    {
        if (_hydrated)
        {
            return;
        }

        await _hydrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hydrated)
            {
                return;
            }

            var loaded = 0;
            var unreadable = 0;
            var root = Path.Combine(_options.RootDirectory, TypingsFolder);
            if (Directory.Exists(root))
            {
                List<string> files;
                try
                {
                    files = Directory
                        .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                        .Order(StringComparer.Ordinal)
                        .ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        ex, "Failed to enumerate news-typing records under '{Root}'.", root);
                    files = [];
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<NewsTypingRecord>(
                            text, RadarFileStoreJson.Options);
                        if (parsed is null
                            || parsed.TypingId == Guid.Empty
                            || string.IsNullOrEmpty(parsed.CohortKey)
                            || string.IsNullOrEmpty(parsed.PayloadHash))
                        {
                            _logger.LogWarning(
                                "News-typing file '{File}' is missing required identity fields; skipping.",
                                file);
                            unreadable++;
                            continue;
                        }

                        if (_byId.TryAdd(parsed.TypingId, parsed))
                        {
                            loaded++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(ex, "Failed to read news-typing record '{File}'; skipping.", file);
                        unreadable++;
                    }
                }
            }

            _logger.LogInformation(
                "Hydrated {Loaded} news-typing record(s) from '{Root}' ({Unreadable} unreadable skipped).",
                loaded,
                _options.RootDirectory,
                unreadable);
            _hydrated = true;
        }
        finally
        {
            _hydrationGate.Release();
        }
    }
}
