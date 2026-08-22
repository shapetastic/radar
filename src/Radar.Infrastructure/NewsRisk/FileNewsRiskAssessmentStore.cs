using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsRisk;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsRisk;

/// <summary>Options for <see cref="FileNewsRiskAssessmentStore"/>: the news-risk output root (assessments live under <c>{root}/assessments/</c>).</summary>
public sealed class FileNewsRiskAssessmentStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// The insert-only durable news-risk assessment store (spec 179 §6), on disk as
/// <c>{root}/assessments/{yyyy}/{MM}/{assessmentId}.json</c>, following the
/// <see cref="Radar.Infrastructure.News.FileNewsObservationArchive"/> mechanism: lazy once-per-instance
/// thread-safe hydration into an id index, deterministic ordinal enumeration, <c>TryAdd</c>-only indexing
/// and <see cref="FileMode.CreateNew"/> writes. The deterministic assessment id (cohort + ordered
/// input-bundle hash + run + reader) makes a re-run of the SAME run idempotent, while any policy/model/input
/// change mints a NEW id — an incompatible assessment is never overwritten.
/// </summary>
public sealed class FileNewsRiskAssessmentStore : INewsRiskAssessmentStore
{
    private const string AssessmentsFolder = "assessments";

    private readonly FileNewsRiskAssessmentStoreOptions _options;
    private readonly ILogger<FileNewsRiskAssessmentStore> _logger;
    private readonly ConcurrentDictionary<Guid, NewsRiskAssessmentRecord> _byId = new();
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    public FileNewsRiskAssessmentStore(
        FileNewsRiskAssessmentStoreOptions options, ILogger<FileNewsRiskAssessmentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<bool> WriteAsync(NewsRiskAssessmentRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        if (!_byId.TryAdd(record.AssessmentId, record))
        {
            // Same deterministic identity ⇒ the same run/reader/input was already durably recorded — a
            // re-run dedupe, never an overwrite.
            return true;
        }

        var partition = record.CreatedAtUtc.UtcDateTime;
        var path = Path.Combine(
            _options.RootDirectory,
            AssessmentsFolder,
            partition.ToString("yyyy", CultureInfo.InvariantCulture),
            partition.ToString("MM", CultureInfo.InvariantCulture),
            record.AssessmentId.ToString("D") + ".json");
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
                "News-risk assessment {AssessmentId} already exists at {Path} (concurrent writer won).",
                record.AssessmentId,
                path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _byId.TryRemove(record.AssessmentId, out _);
            _logger.LogWarning(
                ex, "Failed to write news-risk assessment {AssessmentId} at {Path}.", record.AssessmentId, path);
            return false;
        }
    }

    public async Task<IReadOnlyList<NewsRiskAssessmentRecord>> GetAllAsync(CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);
        return [.. _byId.Values.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.AssessmentId)];
    }

    public async Task<NewsRiskAssessmentRecord?> FindCompletedAsync(
        string cohortKey, string inputBundleHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputBundleHash);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Most recent completed analysis wins; deterministic tiebreak on id (AD-3).
        return _byId.Values
            .Where(r => string.Equals(r.CohortKey, cohortKey, StringComparison.Ordinal)
                && string.Equals(r.InputBundleHash, inputBundleHash, StringComparison.Ordinal)
                && r.IsCompletedAnalysis)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.AssessmentId)
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
            var root = Path.Combine(_options.RootDirectory, AssessmentsFolder);
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
                        ex, "Failed to enumerate news-risk assessments under '{Root}'.", root);
                    files = [];
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<NewsRiskAssessmentRecord>(
                            text, RadarFileStoreJson.Options);
                        if (parsed is null
                            || parsed.AssessmentId == Guid.Empty
                            || string.IsNullOrEmpty(parsed.CohortKey)
                            || string.IsNullOrEmpty(parsed.InputBundleHash))
                        {
                            _logger.LogWarning(
                                "News-risk assessment file '{File}' is missing required identity fields; skipping.",
                                file);
                            unreadable++;
                            continue;
                        }

                        if (_byId.TryAdd(parsed.AssessmentId, parsed))
                        {
                            loaded++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(ex, "Failed to read news-risk assessment '{File}'; skipping.", file);
                        unreadable++;
                    }
                }
            }

            _logger.LogInformation(
                "Hydrated {Loaded} news-risk assessment(s) from '{Root}' ({Unreadable} unreadable skipped).",
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
