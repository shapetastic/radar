using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.Collectors;
using Radar.Application.Evidence;
using Radar.Domain.Evidence;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Insert-only on-disk mirror of the immutable evidence repository. Writes each
/// <see cref="EvidenceItem"/> to
/// <c>{RootDirectory}/{sourceTypeFolder}/{yyyy}/{MM}/{contentHash}.json</c> in the master "Raw
/// Evidence Schema" shape, never overwriting an existing file (provenance, AD-1). All file I/O is
/// confined to Infrastructure; the Application sees only <see cref="IRawEvidenceStore"/>. Disk
/// failures degrade gracefully (warn + skip) and never crash the run.
/// </summary>
public sealed class FileRawEvidenceStore : IRawEvidenceStore
{
    private readonly FileRawEvidenceStoreOptions _options;
    private readonly ILogger<FileRawEvidenceStore> _logger;

    public FileRawEvidenceStore(
        FileRawEvidenceStoreOptions options,
        ILogger<FileRawEvidenceStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<bool> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var observedUtc = (evidence.PublishedAtUtc ?? evidence.CollectedAtUtc).ToUniversalTime();
        var path = Path.Combine(
            _options.RootDirectory,
            SourceTypeFolder(evidence.SourceType),
            observedUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            observedUtc.ToString("MM", CultureInfo.InvariantCulture),
            evidence.ContentHash + ".json");

        // Insert-only (AD-1): an existing final path is a dedupe skip, never an overwrite.
        if (File.Exists(path))
        {
            _logger.LogDebug(
                "Raw evidence file already exists for evidence {EvidenceId} at {Path}; skipping write.",
                evidence.Id,
                path);
            return false;
        }

        var json = Serialize(evidence);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // FileMode.CreateNew throws if the file already exists, so even under a race two writers
            // can never overwrite the same immutable final path. FileOptions.Asynchronous enables
            // true async I/O so WriteAsync doesn't block a thread-pool thread under load.
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using var stream = new FileStream(path, streamOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex) when (File.Exists(path))
        {
            // Expected dedupe race: a concurrent writer won the CreateNew and created the immutable
            // final path first. That is a normal skip, not an I/O failure — log at Debug to avoid
            // noisy warnings during parallel runs.
            _logger.LogDebug(
                ex,
                "Raw evidence file already exists for evidence {EvidenceId} at {Path} (concurrent writer won); skipping write.",
                evidence.Id,
                path);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A genuine disk hiccup (the final path still doesn't exist) must never crash the run;
            // the in-memory repository copy still works.
            _logger.LogWarning(
                ex,
                "Failed to write raw evidence file for evidence {EvidenceId} at {Path}; skipping.",
                evidence.Id,
                path);
            return false;
        }
    }

    /// <summary>
    /// Serializes an <see cref="EvidenceItem"/> into the master "Raw Evidence Schema" field set. The
    /// <c>companyHints</c> array and <c>metadata</c> object are parsed out of the evidence's
    /// <c>MetadataJson</c> (serialized by the <c>CollectedEvidenceMapper</c> as
    /// <c>{ "metadata": {...}, "companyHints": [...] }</c>); a null/blank/unparseable value defaults to
    /// an empty array and an empty object.
    /// </summary>
    private static string Serialize(EvidenceItem evidence)
    {
        var (companyHints, metadata) = ParseMetadataJson(evidence.MetadataJson);

        var raw = new RawEvidenceFile(
            EvidenceId: evidence.Id,
            SourceType: ToSnakeCase(evidence.SourceType.ToString()),
            SourceName: evidence.SourceName,
            SourceUrl: evidence.SourceUrl,
            Title: evidence.Title,
            RawText: evidence.RawText,
            PublishedAt: evidence.PublishedAtUtc,
            CollectedAt: evidence.CollectedAtUtc,
            ContentHash: evidence.ContentHash,
            CompanyHints: companyHints,
            Metadata: metadata);

        return JsonSerializer.Serialize(raw, RadarFileStoreJson.Options);
    }

    private static (string[] CompanyHints, JsonElement Metadata) ParseMetadataJson(string? metadataJson)
    {
        // The hints traversal is shared through the single envelope reader; the metadata element is cloned
        // locally (option (b)) so the serialized RawEvidenceFile JSON stays byte-identical — the shared
        // reader deliberately does not hand back a live JsonElement, and preserving the raw metadata element
        // shape (not a string→string projection) keeps the on-disk output unchanged.
        EvidenceMetadata.TryRead(metadataJson, out _, out var hints);

        var metadata = EmptyObject();

        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return (hints.ToArray(), metadata);
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("metadata", out var metadataElement)
                && metadataElement.ValueKind == JsonValueKind.Object)
            {
                // Clone so the element stays valid after the JsonDocument is disposed.
                metadata = metadataElement.Clone();
            }
        }
        catch (JsonException)
        {
            // Malformed metadata degrades to the empty object; hints already defaulted to [] above.
        }

        return (hints.ToArray(), metadata);
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Maps an <see cref="EvidenceSourceType"/> to its stable on-disk folder. The documented overrides
    /// match the master schema example paths; any other source type defaults to the kebab-cased enum
    /// name (e.g. <c>EarningsTranscript → "earnings-transcript"</c>).
    /// </summary>
    private static string SourceTypeFolder(EvidenceSourceType sourceType) => sourceType switch
    {
        EvidenceSourceType.PressRelease => "press-releases",
        EvidenceSourceType.LocalFile => "local-file",
        EvidenceSourceType.RssFeed => "rss",
        EvidenceSourceType.NewsArticle => "news",
        _ => ToKebabCase(sourceType.ToString()),
    };

    /// <summary>Converts a PascalCase enum name to kebab-case (e.g. <c>EarningsTranscript → earnings-transcript</c>).</summary>
    private static string ToKebabCase(string pascal) => InsertWordBoundary(pascal, '-');

    /// <summary>Converts a PascalCase enum name to snake_case (e.g. <c>PressRelease → press_release</c>).</summary>
    private static string ToSnakeCase(string pascal) => InsertWordBoundary(pascal, '_');

    private static string InsertWordBoundary(string pascal, char separator)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c))
            {
                sb.Append(separator);
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The master "Raw Evidence Schema" field set. Property names render camelCase via the serializer
    /// options (<c>evidenceId</c>, <c>sourceType</c>, …). <c>normalizedText</c> is intentionally omitted.
    /// </summary>
    private sealed record RawEvidenceFile(
        Guid EvidenceId,
        string SourceType,
        string SourceName,
        string? SourceUrl,
        string Title,
        string RawText,
        DateTimeOffset? PublishedAt,
        DateTimeOffset CollectedAt,
        string ContentHash,
        string[] CompanyHints,
        JsonElement Metadata);
}
