using System.Text.Json;
using Microsoft.Extensions.Logging;
using Radar.Application.Collectors;
using Radar.Domain.Evidence;

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

    public EvidenceSourceType SourceType => EvidenceSourceType.LocalFile;

    public async Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct)
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
            return new CollectionResult([], CollectionSummary.Empty);
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
            return new CollectionResult([], CollectionSummary.Empty);
        }

        var items = new List<CollectedEvidence>(files.Count);
        var failures = new List<SourceFailure>();
        var sourcesChecked = 0;
        var sourcesFailed = 0;

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            sourcesChecked++;
            var (item, failureReason) = await ReadDocumentAsync(path, ct).ConfigureAwait(false);
            if (item is not null)
            {
                items.Add(item);
            }
            else
            {
                sourcesFailed++;
                failures.Add(new SourceFailure(
                    Path.GetFileName(path), null, failureReason ?? "Unknown failure"));
            }
        }

        var summary = new CollectionSummary(
            sourcesChecked, sourcesChecked - sourcesFailed, sourcesFailed, items.Count, failures);
        return new CollectionResult(items, summary);
    }

    private async Task<(CollectedEvidence? Item, string? FailureReason)> ReadDocumentAsync(
        string path, CancellationToken ct)
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
            return (null, "Failed to read file");
        }

        LocalFileEvidenceDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<LocalFileEvidenceDocument>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse evidence file '{File}' as JSON; skipping.", fileName);
            return (null, "Invalid JSON");
        }

        if (doc is null
            || string.IsNullOrWhiteSpace(doc.Title)
            || string.IsNullOrWhiteSpace(doc.RawText))
        {
            _logger.LogWarning(
                "Evidence file '{File}' is missing a title or rawText; skipping.",
                fileName);
            return (null, "Missing title or rawText");
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

        var evidence = new CollectedEvidence(
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

        return (evidence, null);
    }
}
