using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsRisk.Evaluation;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsRisk;

/// <summary>Options for <see cref="FileNewsRiskDevelopmentExampleSource"/>: the committed declaration file path.</summary>
public sealed class FileNewsRiskDevelopmentExampleSourceOptions
{
    public required string FilePath { get; init; }
}

/// <summary>
/// Reads the committed <c>docs/cohorts/news-risk-development.json</c> DIRECTLY (spec 179 §8 — the file, not
/// git history, is the declaration mechanism). A missing/unreadable/empty-of-examples file returns
/// <c>null</c> (with a Warning), which the evaluator treats as "the clean prospective table cannot exist"
/// — fail closed, because without the declarations a development example could silently leak into it.
/// </summary>
public sealed class FileNewsRiskDevelopmentExampleSource : INewsRiskDevelopmentExampleSource
{
    private readonly FileNewsRiskDevelopmentExampleSourceOptions _options;
    private readonly ILogger<FileNewsRiskDevelopmentExampleSource> _logger;

    public FileNewsRiskDevelopmentExampleSource(
        FileNewsRiskDevelopmentExampleSourceOptions options,
        ILogger<FileNewsRiskDevelopmentExampleSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NewsRiskDevelopmentExample>?> GetAllAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_options.FilePath))
            {
                _logger.LogWarning(
                    "News-risk development declarations not found at '{Path}'; the clean prospective "
                        + "table will be suppressed (fail closed).",
                    _options.FilePath);
                return null;
            }

            var text = await File.ReadAllTextAsync(_options.FilePath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<DeclarationFile>(text, RadarFileStoreJson.Options);
            if (parsed?.Examples is null)
            {
                _logger.LogWarning(
                    "News-risk development declarations at '{Path}' carry no examples list; the clean "
                        + "prospective table will be suppressed (fail closed).",
                    _options.FilePath);
                return null;
            }

            return parsed.Examples;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read news-risk development declarations at '{Path}'; the clean prospective "
                    + "table will be suppressed (fail closed).",
                _options.FilePath);
            return null;
        }
    }

    private sealed record DeclarationFile(
        string? SchemaVersion, IReadOnlyList<NewsRiskDevelopmentExample>? Examples);
}
