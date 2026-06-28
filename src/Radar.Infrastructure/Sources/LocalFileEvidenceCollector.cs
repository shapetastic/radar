using System.Text.Json;
using Microsoft.Extensions.Logging;
using Radar.Application.Collectors;

namespace Radar.Infrastructure.Sources;

/// <summary>
/// Deterministic test/debug Stage 1 collector. Reads evidence definitions from a local directory of
/// JSON files and produces raw <see cref="CollectedEvidence"/> records. It does NOT normalize, hash,
/// or parse quality — that is the <see cref="CollectedEvidenceMapper"/>'s job; the collector only
/// finds evidence and stamps the collection instant. The collector never persists or mutates
/// evidence; the pipeline runner maps and hands each item to the repository for deduped storage.
/// </summary>
public sealed class LocalFileEvidenceCollector : IEvidenceCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly LocalFileEvidenceCollectorOptions _options;
    private readonly ILogger<LocalFileEvidenceCollector> _logger;
    private readonly TimeProvider _timeProvider;

    public LocalFileEvidenceCollector(
        LocalFileEvidenceCollectorOptions options,
        ILogger<LocalFileEvidenceCollector> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public string CollectorName => "LocalFileEvidenceCollector";

    public string SourceType => "local_file";

    public async Task<IReadOnlyCollection<CollectedEvidence>> CollectAsync(
        CollectionContext context, CancellationToken cancellationToken)
    {
        // The local-file collector is universe-agnostic: it is the deterministic test/debug source
        // and simply emits whatever JSON documents are on disk, so it ignores the watch universe
        // (context). Company-specific collectors (e.g. RSS) consume context for hint resolution.
        _ = context;

        var directory = _options.SourceDirectory;

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning(
                "Local file evidence source directory '{SourceDirectory}' does not exist; returning no evidence.",
                directory);
            return [];
        }

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(directory, "*.json")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to enumerate evidence files in '{SourceDirectory}'; returning no evidence.",
                directory);
            return [];
        }

        var items = new List<CollectedEvidence>(files.Count);

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<CollectedEvidence?> ReadDocumentAsync(string path, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read evidence file '{File}'; skipping.", fileName);
            return null;
        }

        LocalFileEvidenceDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<LocalFileEvidenceDocument>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse evidence file '{File}' as JSON; skipping.", fileName);
            return null;
        }

        if (doc is null
            || string.IsNullOrWhiteSpace(doc.Title)
            || string.IsNullOrWhiteSpace(doc.RawText))
        {
            _logger.LogWarning(
                "Evidence file '{File}' is missing a title or rawText; skipping.",
                fileName);
            return null;
        }

        var sourceName = string.IsNullOrWhiteSpace(doc.SourceName)
            ? Path.GetFileNameWithoutExtension(path)
            : doc.SourceName;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sourceFile"] = fileName,
        };
        if (!string.IsNullOrWhiteSpace(doc.Quality))
        {
            metadata["quality"] = doc.Quality;
        }

        return new CollectedEvidence(
            SourceType: SourceType,
            SourceName: sourceName,
            SourceUrl: doc.SourceUrl,
            Title: doc.Title,
            RawText: doc.RawText,
            PublishedAt: doc.PublishedAtUtc,
            CollectedAt: _timeProvider.GetUtcNow(),
            Metadata: metadata)
        {
            CompanyHints = [],
        };
    }
}
