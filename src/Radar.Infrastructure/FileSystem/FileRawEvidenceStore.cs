using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Evidence;
using Radar.Domain.Evidence;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// Insert-only on-disk mirror of the immutable evidence repository, <b>and</b> the durable
/// <see cref="IEvidenceRepository"/> the scoring path resolves a signal's provenance through (spec 142).
/// Writes each <see cref="EvidenceItem"/> to
/// <c>{RootDirectory}/{sourceTypeFolder}/{yyyy}/{MM}/{contentHash}.json</c> in the master "Raw
/// Evidence Schema" shape, never overwriting an existing file (provenance, AD-1). All file I/O is
/// confined to Infrastructure; the Application sees only <see cref="IRawEvidenceStore"/> /
/// <see cref="IEvidenceRepository"/>. Disk failures degrade gracefully (warn + skip) and never crash the
/// run.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE REPOSITORY IS THE FILE STORE (spec 142's recorded reconciliation choice).</b> Rather than adding
/// a third abstraction wrapping this one, the file store additionally implements
/// <see cref="IEvidenceRepository"/>: one record definition, one deserializer, one set of
/// skip-don't-throw rules, one hydration cache. Composition keeps ONE instance and exposes it under both
/// interfaces (see <c>AddDurableRadarSignalHistory</c>). <c>InMemoryEvidenceRepository</c> stays, for tests.
/// </para>
/// <para>
/// <b>Hydration</b> is lazy (never in the constructor), once per instance, and thread-safe. The first read
/// (or the first <see cref="AddIfNewAsync"/>) walks the tree once and indexes every persisted item by id
/// and by content hash; writes update the index directly. Hydration only ever <c>TryAdd</c>s, so an item
/// this process stored always wins over its own on-disk copy.
/// </para>
/// <para>
/// <b>Behaviour change this creates, stated plainly:</b> with a durable evidence repository,
/// <see cref="AddIfNewAsync"/> now returns <c>false</c> for evidence collected in a PREVIOUS run, so
/// re-running collection no longer re-extracts signals from already-seen evidence. That is the spec's
/// idempotency criterion, and it is a real change to how a live baseline run behaves.
/// </para>
/// </remarks>
public sealed class FileRawEvidenceStore : IRawEvidenceStore, IEvidenceRepository
{
    // Every EvidenceSourceType member, keyed by the snake_case token the file's `sourceType` carries.
    // Built FROM the enum via the same ToSnakeCase used on write, so write and read-back cannot drift and
    // every declared member round-trips by construction.
    private static readonly FrozenDictionary<string, EvidenceSourceType> SourceTypesByToken =
        Enum.GetValues<EvidenceSourceType>()
            .ToFrozenDictionary(t => ToSnakeCase(t.ToString()), t => t, StringComparer.Ordinal);

    private readonly FileRawEvidenceStoreOptions _options;
    private readonly ILogger<FileRawEvidenceStore> _logger;

    private readonly ConcurrentDictionary<Guid, EvidenceItem> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byContentHash = new(StringComparer.Ordinal);

    // Guards the once-per-instance hydration. Deliberately not disposed (the store is not IDisposable):
    // SemaphoreSlim only allocates a disposable WaitHandle if AvailableWaitHandle is read, and it never is
    // here — only WaitAsync/Release, which keeps cancellation working during the first read.
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

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

        var path = PathFor(evidence);

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
            await using (var stream = new FileStream(path, streamOptions))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            // Keep the in-process index in step with the disk so a write is immediately visible to a later
            // repository read. Insert-only, mirroring the file semantics; TryAdd means a later hydration
            // can never clobber it. Deliberately does NOT hydrate — writes stay cheap.
            IndexInsertOnly(evidence);
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
            // A genuine disk hiccup (the final path still doesn't exist) must never crash the run.
            _logger.LogWarning(
                ex,
                "Failed to write raw evidence file for evidence {EvidenceId} at {Path}; skipping.",
                evidence.Id,
                path);
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // IEvidenceRepository — the DURABLE read path (spec 142).
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <b>Index-only, by design</b> — the disk write stays with <see cref="WriteIfNewAsync"/>, which
    /// <c>RadarPipelineRunner</c> already calls immediately after this. Splitting them keeps the
    /// insert-only file semantics (AD-1) and the append-only run behaviour (AD-8) exactly as they were.
    /// Hydrates first, so "new" means new to the ACCRUED store, not merely to this process — that is what
    /// makes re-collection idempotent.
    /// </remarks>
    public async Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        await EnsureHydratedAsync(ct).ConfigureAwait(false);
        return IndexInsertOnly(item);
    }

    public async Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);
        _byId.TryGetValue(id, out var item);
        return item;
    }

    public async Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        if (contentHash is not null
            && _byContentHash.TryGetValue(contentHash, out var id)
            && _byId.TryGetValue(id, out var item))
        {
            return item;
        }

        return null;
    }

    public async Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Deterministic order (AD-3), identical to InMemoryEvidenceRepository's.
        return [.. _byId.Values.OrderBy(e => e.CollectedAtUtc).ThenBy(e => e.Id)];
    }

    /// <summary>
    /// The atomic check-and-add shared by <see cref="AddIfNewAsync"/> and <see cref="WriteIfNewAsync"/>:
    /// the content-hash index enforces the unique-hash dedupe rule, and the id index preserves
    /// immutability (an existing record under the same id is never overwritten). A failed id insert rolls
    /// the hash entry back so the two indexes stay consistent. Mirrors
    /// <c>InMemoryEvidenceRepository.AddIfNewAsync</c> exactly.
    /// </summary>
    private bool IndexInsertOnly(EvidenceItem item)
    {
        if (!_byContentHash.TryAdd(item.ContentHash, item.Id))
        {
            return false;
        }

        if (!_byId.TryAdd(item.Id, item))
        {
            _byContentHash.TryRemove(item.ContentHash, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Loads every persisted raw-evidence file into the in-memory indexes, exactly once per instance.
    /// Lazy (never in the constructor) and thread-safe: concurrent first callers queue on the gate and
    /// only one walks the tree. Files are visited in ORDINAL PATH ORDER so that, where duplicate content
    /// hashes exist on disk, the surviving item is a function of the path alone rather than of the
    /// undefined order <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> returns.
    /// Per-file failures are logged and SKIPPED, never thrown — including a file
    /// whose <c>sourceType</c> cannot be parsed back, because <see cref="EvidenceItem.SourceType"/> feeds
    /// attention breadth/diversity in the v8 formula and guessing it would corrupt a score more quietly
    /// than dropping the item does. <see cref="OperationCanceledException"/> still propagates.
    /// </summary>
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
            var skipped = 0;

            if (Directory.Exists(_options.RootDirectory))
            {
                // Ordinal-sorted, NOT raw enumeration order: hydration de-dupes by ContentHash and
                // TryAdds, so when two files carry the same hash (they do — the mapper mints a fresh
                // evidence Guid per run, and copies can land under different source-type folders) the
                // FIRST file read wins. Directory.EnumerateFiles has no defined order, so an unsorted
                // walk would let the winning item — and therefore the scored evidence set — vary between
                // runs and between OSes. Sorting makes the survivor a function of the path alone.
                foreach (var file in EnumerateEvidenceFiles().Order(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<RawEvidenceFile>(text, RadarFileStoreJson.Options);
                        if (parsed is null)
                        {
                            _logger.LogWarning(
                                "Raw evidence file '{File}' contained a null record; skipping.", file);
                            skipped++;
                            continue;
                        }

                        var item = ToEvidenceItem(parsed, file);
                        if (item is null)
                        {
                            skipped++;
                            continue;
                        }

                        if (IndexInsertOnly(item))
                        {
                            loaded++;
                        }
                        else
                        {
                            // A duplicate content hash or id — the earlier (ordinal-first) file already
                            // holds this evidence. Counted, not silent: the log line claims to report
                            // duplicates, and the duplication rate is the number that tells us whether
                            // evidence identity (spec 141) still needs fixing.
                            skipped++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(ex, "Failed to read raw evidence file '{File}'; skipping.", file);
                        skipped++;
                    }
                }
            }

            _logger.LogInformation(
                "Hydrated {Loaded} raw evidence item(s) from '{Root}' ({Skipped} unreadable/duplicate file(s) skipped).",
                loaded,
                _options.RootDirectory,
                skipped);

            _hydrated = true;
        }
        finally
        {
            _hydrationGate.Release();
        }
    }

    /// <summary>
    /// Enumerates every <c>*.json</c> under the root. Enumeration failures degrade to "no more files"
    /// rather than aborting hydration.
    /// </summary>
    private IEnumerable<string> EnumerateEvidenceFiles()
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory
                .EnumerateFiles(_options.RootDirectory, "*.json", SearchOption.AllDirectories)
                .GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to enumerate raw evidence files under '{Root}'; hydrating nothing.",
                _options.RootDirectory);
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed while enumerating raw evidence files under '{Root}'; stopping enumeration early.",
                        _options.RootDirectory);
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    /// <summary>
    /// Reconstructs an <see cref="EvidenceItem"/> from its persisted shape, or <c>null</c> (logged) when
    /// the file cannot be honestly reconstructed.
    /// <para>
    /// <b><c>Quality</c> (a v8 formula input) resolution order:</b>
    /// </para>
    /// <list type="number">
    /// <item>the explicit top-level <c>quality</c> field — authoritative, written by every new file;</item>
    /// <item>otherwise the persisted <c>metadata.quality</c>, parsed with the EXACT
    /// <see cref="EvidenceQualityParser"/> rule <c>CollectedEvidenceMapper</c> applied at collection time.
    /// This is a RECOVERY of the value the item actually carried when it was scored live, not a fabricated
    /// default — the collector's declared quality has been persisted in the metadata bag all along;</item>
    /// <item>otherwise <see cref="EvidenceQuality.Unknown"/> — which is exactly what the mapper itself
    /// produces for evidence that declared no quality, and whose weight
    /// (<c>ScoringWeights.QualityUnknown</c> = 0.40) sits BELOW Medium (0.60) / High (0.85) /
    /// PrimarySource (1.00), so it cannot flatter a score.</item>
    /// </list>
    /// <para>
    /// <c>MetadataJson</c> is re-composed through the same <see cref="EvidenceMetadata.Compose"/> the
    /// mapper authors it with, from the file's separate <c>metadata</c>/<c>companyHints</c> nodes, so the
    /// envelope is byte-identical by construction. <c>CollectedEvidence.Metadata</c> is
    /// <c>string→string</c>, so the string-valued projection is lossless.
    /// </para>
    /// </summary>
    private EvidenceItem? ToEvidenceItem(RawEvidenceFile parsed, string file)
    {
        if (!SourceTypesByToken.TryGetValue(parsed.SourceType ?? string.Empty, out var sourceType))
        {
            // Never guess: SourceType feeds attention breadth/diversity in the v8 formula, so an
            // unparseable value degrades the FILE (skip) rather than the SCORE (a wrong source type).
            _logger.LogWarning(
                "Raw evidence file '{File}' declares unknown sourceType '{SourceType}'; skipping.",
                file,
                parsed.SourceType);
            return null;
        }

        // Provenance completeness: an item indexed under a null/blank content hash could not dedupe, and one
        // with a null source name / title / body carries no usable provenance. Skip the FILE rather than
        // materialise a half-item that would go on to back a score.
        if (string.IsNullOrEmpty(parsed.ContentHash)
            || parsed.SourceName is null
            || parsed.Title is null
            || parsed.RawText is null)
        {
            _logger.LogWarning(
                "Raw evidence file '{File}' is missing a required field (contentHash/sourceName/title/rawText); skipping.",
                file);
            return null;
        }

        var metadata = EvidenceMetadata.ReadMetadataObject(parsed.Metadata);
        var hints = parsed.CompanyHints ?? [];

        var quality = parsed.Quality
            ?? EvidenceQualityParser.Parse(metadata.GetValueOrDefault("quality"));

        return new EvidenceItem(
            Id: parsed.EvidenceId,
            SourceType: sourceType,
            SourceName: parsed.SourceName,
            SourceUrl: parsed.SourceUrl,
            Title: parsed.Title,
            Summary: parsed.Summary,
            RawText: parsed.RawText,
            ContentHash: parsed.ContentHash,
            PublishedAtUtc: parsed.PublishedAt,
            CollectedAtUtc: parsed.CollectedAt,
            Quality: quality,
            MetadataJson: EvidenceMetadata.Compose(metadata, hints));
    }

    private string PathFor(EvidenceItem evidence)
    {
        var observedUtc = (evidence.PublishedAtUtc ?? evidence.CollectedAtUtc).ToUniversalTime();
        return Path.Combine(
            _options.RootDirectory,
            SourceTypeFolder(evidence.SourceType),
            observedUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            observedUtc.ToString("MM", CultureInfo.InvariantCulture),
            evidence.ContentHash + ".json");
    }

    /// <summary>
    /// Serializes an <see cref="EvidenceItem"/> into the master "Raw Evidence Schema" field set. The
    /// <c>companyHints</c> array and <c>metadata</c> object are parsed out of the evidence's
    /// <c>MetadataJson</c> (composed by the <c>CollectedEvidenceMapper</c> as
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
            Metadata: metadata,
            Quality: evidence.Quality,
            Summary: evidence.Summary);

        return JsonSerializer.Serialize(raw, RadarFileStoreJson.Options);
    }

    private static (IReadOnlyList<string> CompanyHints, JsonElement Metadata) ParseMetadataJson(string? metadataJson)
    {
        // The hints traversal is shared through the single envelope reader, which already materialises them
        // into an owned array — pass that through directly rather than copying it again. The metadata element
        // is cloned locally (option (b)) so the serialized RawEvidenceFile JSON stays byte-identical — the
        // shared reader deliberately does not hand back a live JsonElement, and preserving the raw metadata
        // element shape (not a string→string projection) keeps the on-disk output unchanged.
        EvidenceMetadata.TryRead(metadataJson, out _, out var hints);

        var metadata = EmptyObject();

        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return (hints, metadata);
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

        return (hints, metadata);
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
    /// <para>
    /// <c>Quality</c> and <c>Summary</c> are TRAILING and NULLABLE (spec 142) so every pre-existing file
    /// still deserializes. <c>quality</c> is the authoritative value for new writes — it is a v8 formula
    /// input, and hydrating evidence without it would silently score history differently from how it was
    /// scored live. <c>summary</c> is written only when non-null (production always writes null, so the
    /// on-disk shape of a real file is unchanged) and exists so the round-trip is genuinely lossless
    /// rather than green by accident.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The reference-typed members are declared NULLABLE because deserialization is the honest source of
    /// truth about what a file on disk actually contains — a truncated or hand-edited file can omit any of
    /// them, and STJ will hand back null regardless of the declared nullability. <see cref="ToEvidenceItem"/>
    /// therefore validates them and skips the file rather than materialising an evidence item with null
    /// provenance. The write path always supplies real values, so the serialized output is unchanged.
    /// </remarks>
    private sealed record RawEvidenceFile(
        Guid EvidenceId,
        string? SourceType,
        string? SourceName,
        string? SourceUrl,
        string? Title,
        string? RawText,
        DateTimeOffset? PublishedAt,
        DateTimeOffset CollectedAt,
        string? ContentHash,
        IReadOnlyList<string>? CompanyHints,
        JsonElement Metadata,
        EvidenceQuality? Quality,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary);
}
