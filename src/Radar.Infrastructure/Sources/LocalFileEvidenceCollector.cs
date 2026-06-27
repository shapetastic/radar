using System.Text.Json;
using Microsoft.Extensions.Logging;
using Radar.Application.Collectors;
using Radar.Application.Evidence;
using Radar.Domain.Evidence;

namespace Radar.Infrastructure.Sources;

/// <summary>
/// Deterministic Stage 1 collector. Reads evidence definitions from a local directory of JSON
/// files and produces immutable <see cref="EvidenceItem"/> records, computing normalized text and
/// content hash via <see cref="IEvidenceNormalizer"/>. The collector never persists or mutates
/// evidence; a later worker job hands each item to the repository for deduped storage.
/// </summary>
public sealed class LocalFileEvidenceCollector : IEvidenceCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IEvidenceNormalizer _normalizer;
    private readonly LocalFileEvidenceCollectorOptions _options;
    private readonly ILogger<LocalFileEvidenceCollector> _logger;
    private readonly TimeProvider _timeProvider;

    public LocalFileEvidenceCollector(
        IEvidenceNormalizer normalizer,
        LocalFileEvidenceCollectorOptions options,
        ILogger<LocalFileEvidenceCollector> logger,
        TimeProvider timeProvider)
    {
        _normalizer = normalizer;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<EvidenceItem>> CollectAsync(CancellationToken ct)
    {
        var directory = _options.SourceDirectory;

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning(
                "Local file evidence source directory '{SourceDirectory}' does not exist; returning no evidence.",
                directory);
            return [];
        }

        var files = Directory
            .EnumerateFiles(directory, "*.json")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        var items = new List<EvidenceItem>(files.Count);

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            var item = await ReadDocumentAsync(path, ct).ConfigureAwait(false);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<EvidenceItem?> ReadDocumentAsync(string path, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
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

        var normalized = _normalizer.Normalize(doc.Title, doc.RawText);

        var sourceName = string.IsNullOrWhiteSpace(doc.SourceName)
            ? Path.GetFileNameWithoutExtension(path)
            : doc.SourceName;

        var metadataJson = JsonSerializer.Serialize(new { sourceFile = fileName });

        return new EvidenceItem(
            Id: Guid.NewGuid(),
            SourceType: EvidenceSourceType.LocalFile,
            SourceName: sourceName,
            SourceUrl: doc.SourceUrl,
            Title: doc.Title,
            Summary: doc.Summary,
            RawText: normalized.NormalizedText,
            ContentHash: normalized.ContentHash,
            PublishedAtUtc: doc.PublishedAtUtc?.ToUniversalTime(),
            CollectedAtUtc: _timeProvider.GetUtcNow(),
            Quality: EvidenceQuality.Unknown,
            MetadataJson: metadataJson);
    }
}
