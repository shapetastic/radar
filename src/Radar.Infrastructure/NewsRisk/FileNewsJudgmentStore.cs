using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsRisk;

/// <summary>Options for <see cref="FileNewsJudgmentStore"/>: the news-risk output root (judgments live under <c>{root}/judgments/</c>).</summary>
public sealed class FileNewsJudgmentStoreOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// The insert-only durable direction-judgment store (spec 185 §5), on disk as
/// <c>{root}/judgments/{judge-policy-segment}/{companyId}/{judgmentId}.json</c> — the policy segment is
/// LAYOUT only (the shared <see cref="NewsTypingCohortPath"/> encoding); the record's <c>CohortKey</c>
/// FIELD stays the authoritative identity. Follows the <see cref="FileNewsRiskAssessmentStore"/> mechanism:
/// lazy once-per-instance thread-safe hydration into an id index, deterministic ordinal enumeration,
/// <c>TryAdd</c>-only indexing and <see cref="FileMode.CreateNew"/> writes; a malformed file is logged and
/// skipped, never thrown.
/// </summary>
public sealed class FileNewsJudgmentStore : INewsJudgmentStore
{
    private readonly FileNewsJudgmentStoreOptions _options;
    private readonly ILogger<FileNewsJudgmentStore> _logger;
    private readonly ConcurrentDictionary<Guid, NewsJudgmentRecord> _byId = new();
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    public FileNewsJudgmentStore(FileNewsJudgmentStoreOptions options, ILogger<FileNewsJudgmentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        if (!_byId.TryAdd(record.JudgmentId, record))
        {
            // Same deterministic identity ⇒ the same run/judge/company/family-set was already durably
            // recorded — a re-run dedupe, never an overwrite.
            return true;
        }

        var path = Path.Combine(
            _options.RootDirectory,
            NewsJudgmentStoreLayout.JudgmentsFolder,
            NewsTypingCohortPath.PolicySegment(record.Provider, record.ModelId),
            record.CompanyId.ToString("D"),
            record.JudgmentId.ToString("D") + ".json");
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
                "News-judgment record {JudgmentId} already exists at {Path} (concurrent writer won).",
                record.JudgmentId,
                path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _byId.TryRemove(record.JudgmentId, out _);
            _logger.LogWarning(
                ex, "Failed to write news-judgment record {JudgmentId} at {Path}.", record.JudgmentId, path);
            return false;
        }
    }

    public async Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);
        return [.. _byId.Values.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.JudgmentId)];
    }

    public async Task<NewsJudgmentRecord?> FindCompletedAsync(
        string cohortKey, Guid companyId, string familySetHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(familySetHash);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Most recent completed judgment wins; deterministic tiebreak on id (AD-3).
        return _byId.Values
            .Where(r => string.Equals(r.CohortKey, cohortKey, StringComparison.Ordinal)
                && r.CompanyId == companyId
                && string.Equals(r.FamilySetHash, familySetHash, StringComparison.Ordinal)
                && r.IsCompletedJudgment)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.JudgmentId)
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
            var root = NewsJudgmentStoreLayout.RootFor(_options.RootDirectory);
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
                        ex, "Failed to enumerate news-judgment records under '{Root}'.", root);
                    files = [];
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<NewsJudgmentRecord>(
                            text, RadarFileStoreJson.Options);
                        if (parsed is null
                            || parsed.JudgmentId == Guid.Empty
                            || string.IsNullOrEmpty(parsed.CohortKey)
                            || string.IsNullOrEmpty(parsed.FamilySetHash))
                        {
                            _logger.LogWarning(
                                "News-judgment file '{File}' is missing required identity fields; skipping.",
                                file);
                            unreadable++;
                            continue;
                        }

                        if (_byId.TryAdd(parsed.JudgmentId, parsed))
                        {
                            loaded++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(ex, "Failed to read news-judgment record '{File}'; skipping.", file);
                        unreadable++;
                    }
                }
            }

            _logger.LogInformation(
                "Hydrated {Loaded} news-judgment record(s) from '{Root}' ({Unreadable} unreadable skipped).",
                loaded,
                root,
                unreadable);
            _hydrated = true;
        }
        finally
        {
            _hydrationGate.Release();
        }
    }
}
