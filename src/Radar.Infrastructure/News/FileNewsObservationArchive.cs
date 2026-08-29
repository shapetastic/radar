using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.News;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.News;

/// <summary>
/// The insert-only, id-indexed point-in-time news observation archive (spec 177 §§4–5), on disk as
/// <c>{root}/observations/{yyyy}/{MM}/{observationId}.json</c> plus per-pass batch manifests under
/// <c>{root}/batches/</c> and the create-once <c>{root}/boundary.json</c>. It follows
/// <see cref="FileRawEvidenceStore"/>'s spec-142 mechanism deliberately: lazy once-per-instance thread-safe
/// hydration into a process-wide <c>observationId → record</c> index, deterministic ordinal path
/// enumeration, <c>TryAdd</c>-only indexing and <see cref="FileMode.CreateNew"/> writes so concurrent
/// writers can never overwrite an immutable file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hydrated index — not the path — is the dedupe mechanism.</b> The partition derives from the
/// record's immutable <c>FirstObservedAtUtc</c>, so re-observing the same payload in a later month would
/// derive a DIFFERENT path; a path-existence check alone would happily write a second copy there. Every
/// write therefore consults the fully hydrated index FIRST: an already-indexed id with the SAME payload
/// hash is a cross-run dedupe (the original record and its earliest first-observed instant survive,
/// untouched), while an already-indexed id with a DIFFERENT payload hash is a CONFLICT — fail closed,
/// never a dedupe, because by construction the id derives from the hash and a mismatch means a corrupted
/// or hand-edited record.
/// </para>
/// <para>
/// <b>Hydration classification mirrors spec 145's two-counter rule:</b> a duplicate file carrying the
/// identical record (a legacy artifact of the pre-index era) collapses to the ordinal-first copy — the
/// file is retained and the collapse is counted/reported, nothing lost — whereas a file whose id is
/// already held by a different payload hash is counted, and logged at Warning, as an
/// unreadable/conflicting record. The two numbers mean different things and are never summed.
/// </para>
/// <para>
/// All disk failures degrade gracefully (Warning + typed outcome); nothing here can abort a pipeline run.
/// The archive feeds no evidence, no signal, no score and no fingerprint.
/// </para>
/// </remarks>
public sealed class FileNewsObservationArchive
    : INewsObservationArchive,
        INewsObservationBatchReader,
        INewsProspectiveBoundaryReader,
        INewsObservationCompanyHistory
{
    private const string ObservationsFolder = "observations";
    private const string BatchesFolder = "batches";
    private const string BoundaryFileName = "boundary.json";

    private readonly NewsObservationArchiveOptions _options;
    private readonly ILogger<FileNewsObservationArchive> _logger;

    private readonly ConcurrentDictionary<Guid, NewsObservationRecord> _byId = new();

    // Guards the once-per-instance hydration. Same non-disposal rationale as FileRawEvidenceStore: only
    // WaitAsync/Release are used, so no WaitHandle is ever allocated.
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    public FileNewsObservationArchive(
        NewsObservationArchiveOptions options,
        ILogger<FileNewsObservationArchive> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<NewsObservationWriteOutcome> WriteAsync(
        NewsObservationRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Consult the index BEFORE deriving any path: this is what makes the dedupe cross-partition and
        // what preserves the original earliest FirstObservedAtUtc (the indexed record simply wins).
        if (!_byId.TryAdd(record.ObservationId, record))
        {
            return ClassifyExisting(record);
        }

        var path = PathFor(record);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // CreateNew: even under a cross-process race two writers can never overwrite one immutable file.
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(path, streamOptions))
            {
                var bytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(record, RadarFileStoreJson.Options));
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            return NewsObservationWriteOutcome.Written;
        }
        catch (IOException ex) when (File.Exists(path))
        {
            // A concurrent OS-level writer won the CreateNew for the same immutable path — a normal dedupe
            // race, not data loss. The index entry we added stands (it describes the same record).
            _logger.LogDebug(
                ex,
                "News observation file already exists for {ObservationId} at {Path} (concurrent writer won); skipping write.",
                record.ObservationId,
                path);
            return NewsObservationWriteOutcome.CrossRunDeduped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A genuine disk failure: roll the index entry back so a later retry can write, and report
            // Failed so the batch records this run as UNPROVEN capture rather than a clean zero.
            _byId.TryRemove(record.ObservationId, out _);
            _logger.LogWarning(
                ex,
                "Failed to write news observation {ObservationId} at {Path}; this run's capture is unproven for it.",
                record.ObservationId,
                path);
            return NewsObservationWriteOutcome.Failed;
        }
    }

    public async Task<bool> WriteBatchAsync(NewsObservationBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var fileToken = batch.RunAsOfUtc.UtcDateTime
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var json = JsonSerializer.Serialize(batch, RadarFileStoreJson.Options);

        // Manifests are as-of-named per the spec's layout, but they are run records and as immutable as
        // observation files, so never-overwrite must be structural (FileMode.CreateNew), not a check-then-
        // write that a concurrent process could race past. A taken token (two batches within one second,
        // or the race itself) falls back ONCE to a batch-id-suffixed name — the id is unique per batch,
        // so a second exists collision can only mean this very manifest is already on disk.
        var path = Path.Combine(_options.RootDirectory, BatchesFolder, fileToken + ".json");
        var outcome = await TryCreateManifestAsync(path, json, ct).ConfigureAwait(false);
        if (outcome == ManifestWriteOutcome.PathTaken)
        {
            path = Path.Combine(
                _options.RootDirectory,
                BatchesFolder,
                fileToken + "-" + batch.BatchId.ToString("N") + ".json");
            outcome = await TryCreateManifestAsync(path, json, ct).ConfigureAwait(false);
            if (outcome == ManifestWriteOutcome.PathTaken)
            {
                _logger.LogWarning(
                    "News observation batch manifest already exists at {Path}; refusing to overwrite it.",
                    path);
            }
        }

        var written = outcome == ManifestWriteOutcome.Written;
        if (written)
        {
            await TryEstablishBoundaryAsync(batch, ct).ConfigureAwait(false);
        }

        return written;
    }

    public async Task<IReadOnlyList<NewsObservationRecord>> GetAllAsync(CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Deterministic order (AD-3).
        return [.. _byId.Values.OrderBy(o => o.FirstObservedAtUtc).ThenBy(o => o.ObservationId)];
    }

    /// <summary>
    /// SPEC 198 §2 — the company ids that already hold at least one archived observation, read off the SAME
    /// lazily-hydrated <c>_byId</c> index every other read uses. No second store, no side index and no
    /// second deserializer (spec 142's "the repository IS the file store" precedent, and spec 151's recorded
    /// rejection of a materialized side index that can drift from the store it summarises).
    /// <para>
    /// Records carrying no company contribute nothing: an unattributed observation is not evidence that
    /// Radar has ever observed any particular company. The result is a plain set, deterministic by
    /// construction (membership, not order), and the caller treats it as read-only.
    /// </para>
    /// </summary>
    public async Task<IReadOnlySet<Guid>> GetCompaniesWithObservationsAsync(CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        var companies = new HashSet<Guid>();
        foreach (var record in _byId.Values)
        {
            if (record.CompanyId is { } companyId)
            {
                companies.Add(companyId);
            }
        }

        return companies;
    }

    /// <summary>
    /// An id already indexed: identical payload hash ⇒ cross-run dedupe (the original record — and its
    /// earliest first-observed instant — survives); a DIFFERENT payload hash ⇒ fail-closed conflict. The
    /// second case cannot arise from honest writes (the id is a function of the hash), so it is corruption
    /// detection, logged at Warning and counted by the caller as a failure.
    /// </summary>
    private NewsObservationWriteOutcome ClassifyExisting(NewsObservationRecord record)
    {
        if (_byId.TryGetValue(record.ObservationId, out var existing)
            && !string.Equals(existing.PayloadHash, record.PayloadHash, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "News observation id {ObservationId} is already archived with payload hash "
                    + "'{ExistingHash}' but an incoming record claims '{IncomingHash}'; refusing the write "
                    + "(fail closed — this is a conflicting record, never a dedupe).",
                record.ObservationId,
                existing.PayloadHash,
                record.PayloadHash);
            return NewsObservationWriteOutcome.Conflict;
        }

        return NewsObservationWriteOutcome.CrossRunDeduped;
    }

    private enum ManifestWriteOutcome
    {
        Written,
        PathTaken,
        Failed,
    }

    /// <summary>
    /// One <see cref="FileMode.CreateNew"/> manifest write attempt: <see cref="ManifestWriteOutcome.PathTaken"/>
    /// when another writer holds the path (the caller picks a different name — never overwrites), and the
    /// usual graceful degradation (Warning + <see cref="ManifestWriteOutcome.Failed"/>) on a genuine disk
    /// failure so a hiccup never aborts the run.
    /// </summary>
    private async Task<ManifestWriteOutcome> TryCreateManifestAsync(
        string path, string json, CancellationToken ct)
    {
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
            var bytes = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            return ManifestWriteOutcome.Written;
        }
        catch (IOException ex) when (File.Exists(path))
        {
            _logger.LogDebug(
                ex,
                "News observation batch manifest path {Path} is already taken; it will not be overwritten.",
                path);
            return ManifestWriteOutcome.PathTaken;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Failed to write news observation batch manifest at {Path}; skipping.", path);
            return ManifestWriteOutcome.Failed;
        }
    }

    /// <summary>
    /// Creates <c>boundary.json</c> ONCE, on the first successful post-spec-177 FULL-UNIVERSE prospective
    /// batch: <c>firstProspectiveCaptureAsOfUtc</c> is that actual run's as-of instant, never a date from a
    /// document. <see cref="FileMode.CreateNew"/> makes create-once structural — an existing boundary is
    /// left byte-untouched forever. A company-filtered pass (FullUniverse=false) and an unproven batch
    /// (failures &gt; 0) may capture observations but can never establish the boundary.
    /// </summary>
    private async Task TryEstablishBoundaryAsync(NewsObservationBatch batch, CancellationToken ct)
    {
        if (!batch.FullUniverse || !batch.CaptureProven)
        {
            return;
        }

        var path = Path.Combine(_options.RootDirectory, BoundaryFileName);
        if (File.Exists(path))
        {
            return;
        }

        var boundary = new NewsObservationBoundary(
            SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
            FirstProspectiveCaptureAsOfUtc: batch.RunAsOfUtc,
            EstablishedByBatchId: batch.BatchId);

        try
        {
            Directory.CreateDirectory(_options.RootDirectory);
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using var stream = new FileStream(path, streamOptions);
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(boundary, RadarFileStoreJson.Options));
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "News observation prospective boundary established: firstProspectiveCaptureAsOfUtc={AsOf:o} "
                    + "(batch {BatchId}).",
                batch.RunAsOfUtc,
                batch.BatchId);
        }
        catch (IOException) when (File.Exists(path))
        {
            // A concurrent writer established it first — create-once holds; nothing to do.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed boundary write is only a delay: the NEXT successful full-universe batch establishes
            // it (with its own honest as-of), so nothing needs to be retried here.
            _logger.LogWarning(
                ex, "Failed to write news observation boundary at {Path}; a later batch will establish it.", path);
        }
    }

    private string PathFor(NewsObservationRecord record)
    {
        var partition = record.FirstObservedAtUtc.ToUniversalTime();
        return Path.Combine(
            _options.RootDirectory,
            ObservationsFolder,
            partition.ToString("yyyy", CultureInfo.InvariantCulture),
            partition.ToString("MM", CultureInfo.InvariantCulture),
            record.ObservationId.ToString("D") + ".json");
    }

    /// <summary>
    /// Loads every persisted observation into the id index, exactly once per instance — lazy, thread-safe,
    /// ordinal path order (so where legacy duplicate files exist, the survivor is a function of the path
    /// alone, and the earliest-partitioned copy of an id wins deterministically). <c>TryAdd</c>-only, so a
    /// record this process wrote always beats its own on-disk copy. Per-file failures are logged and
    /// skipped, never thrown; <see cref="OperationCanceledException"/> propagates.
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
            // TWO counters, deliberately (the spec-145 precedent): an identical-record collapse loses
            // nothing and measures legacy duplication; a conflicting/unreadable file is data loss.
            var duplicatesCollapsed = 0;
            var unreadable = 0;

            var observationsRoot = Path.Combine(_options.RootDirectory, ObservationsFolder);
            if (Directory.Exists(observationsRoot))
            {
                foreach (var file in EnumerateObservationFiles(observationsRoot).Order(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<NewsObservationRecord>(
                            text, RadarFileStoreJson.Options);
                        if (parsed is null
                            || parsed.ObservationId == Guid.Empty
                            || string.IsNullOrEmpty(parsed.PayloadHash)
                            || parsed.GoogleLandingUrl is null
                            || parsed.Headline is null)
                        {
                            _logger.LogWarning(
                                "News observation file '{File}' is missing required identity/provenance "
                                    + "fields; skipping.",
                                file);
                            unreadable++;
                            continue;
                        }

                        if (_byId.TryAdd(parsed.ObservationId, parsed))
                        {
                            loaded++;
                        }
                        else if (_byId.TryGetValue(parsed.ObservationId, out var indexed)
                            && string.Equals(indexed.PayloadHash, parsed.PayloadHash, StringComparison.Ordinal))
                        {
                            // Ordinal-first identical record wins; this file is RETAINED on disk and only
                            // the identity index collapses. Nothing lost ⇒ Debug + its own counter.
                            duplicatesCollapsed++;
                            _logger.LogDebug(
                                "News observation file '{File}' duplicates already-indexed observation "
                                    + "{ObservationId}; collapsing to the ordinal-first record (file retained).",
                                file,
                                parsed.ObservationId);
                        }
                        else
                        {
                            // One id, two DIFFERENT payload hashes: an unreadable/conflicting record, never
                            // a dedupe (spec 177 §4). This file's content is dropped from the index ⇒
                            // Warning + the loss counter.
                            unreadable++;
                            _logger.LogWarning(
                                "News observation file '{File}' declares id {ObservationId} with payload "
                                    + "hash '{Hash}', which is already indexed with a DIFFERENT hash; "
                                    + "skipping this file (fail closed).",
                                file,
                                parsed.ObservationId,
                                parsed.PayloadHash);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(ex, "Failed to read news observation file '{File}'; skipping.", file);
                        unreadable++;
                    }
                }
            }

            _logger.LogInformation(
                "Hydrated {Loaded} news observation(s) from '{Root}' ({DuplicateFiles} duplicate file(s) "
                    + "collapsed, {UnreadableFiles} unreadable/conflicting file(s) skipped).",
                loaded,
                _options.RootDirectory,
                duplicatesCollapsed,
                unreadable);

            _hydrated = true;
        }
        finally
        {
            _hydrationGate.Release();
        }
    }

    /// <summary>Enumeration failures degrade to "no more files" rather than aborting hydration.</summary>
    private IEnumerable<string> EnumerateObservationFiles(string root)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory
                .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Failed to enumerate news observation files under '{Root}'; hydrating nothing.", root);
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
                        "Failed while enumerating news observation files under '{Root}'; stopping enumeration early.",
                        root);
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    /// <summary>
    /// Resolves one batch manifest by explicit batch id (spec 179 §4). Manifests are as-of-named on disk, so
    /// this enumerates <c>{root}/batches/*.json</c> in deterministic ordinal order and matches on the
    /// serialized <c>BatchId</c>. Every read failure degrades to <c>null</c> (Warning) — the caller must
    /// treat that as UNPROVEN capture, never as a clean batch; cancellation propagates.
    /// </summary>
    public async Task<NewsObservationBatch?> GetBatchAsync(Guid batchId, CancellationToken ct)
    {
        var batchesRoot = Path.Combine(_options.RootDirectory, BatchesFolder);
        if (!Directory.Exists(batchesRoot))
        {
            return null;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(batchesRoot, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Failed to enumerate news observation batch manifests under '{Root}'.", batchesRoot);
            return null;
        }

        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<NewsObservationBatch>(text, RadarFileStoreJson.Options);
                if (parsed is not null && parsed.BatchId == batchId)
                {
                    return parsed;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(
                    ex, "Failed to read news observation batch manifest '{File}'; skipping.", file);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the create-once <c>boundary.json</c> (spec 179 §9). <c>null</c> when it has not been
    /// established or cannot be read — fail closed at the caller: no boundary means no clean prospective
    /// cohort, never "everything is prospective".
    /// </summary>
    public async Task<NewsObservationBoundary?> ReadBoundaryAsync(CancellationToken ct)
    {
        var path = Path.Combine(_options.RootDirectory, BoundaryFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NewsObservationBoundary>(text, RadarFileStoreJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to read news observation boundary at {Path}.", path);
            return null;
        }
    }
}
