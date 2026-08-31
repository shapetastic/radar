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
using Radar.Application.Storage;
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
/// <para>
/// <b>Evidence identity is content-derived from spec 145 on</b>
/// (<see cref="Radar.Application.Evidence.EvidenceIdentity"/>), so the id a signal references and the id
/// this store's <c>contentHash</c>-keyed file carries finally agree. <b>Accrued history is left exactly as
/// it is</b> — the chosen option, deliberately: legacy files keep their legacy per-run ids, this store
/// never rewrites or deletes one (insert-only, AD-1), and there is no backfill, migration or "supersede"
/// marker. Retro-healing resolution would turn the live 30-day window's 2,618 scored signals into 12,145
/// (~4.6× inflation), so historical series stay exactly as they were actually scored. Duplicate-content
/// files that predate 145 therefore remain, and hydration COUNTS them separately from unreadable files so
/// the residual duplication rate stays visible instead of hiding inside a single "skipped" tally. That
/// split is by CAUSE: only a same-content collapse (which loses nothing) counts as a duplicate; a file
/// whose evidence id is already held by DIFFERENT content has its content dropped, so it is counted — and
/// logged at Warning — as data loss.
/// </para>
/// </remarks>
public sealed class FileRawEvidenceStore : IRawEvidenceStore, IEvidenceRepository, IHydrationTelemetry
{
    // Every EvidenceSourceType member, keyed by the snake_case token the file's `sourceType` carries.
    // Built FROM the enum via the same ToSnakeCase used on write, so write and read-back cannot drift and
    // every declared member round-trips by construction.
    private static readonly FrozenDictionary<string, EvidenceSourceType> SourceTypesByToken =
        Enum.GetValues<EvidenceSourceType>()
            .ToFrozenDictionary(t => ToSnakeCase(t.ToString()), t => t, StringComparer.Ordinal);

    private readonly FileRawEvidenceStoreOptions _options;
    private readonly ILogger<FileRawEvidenceStore> _logger;
    private readonly TimeProvider _timeProvider;

    // Spec 203 §1: the measured hydration walk (monotonic). Null until this instance hydrates.
    private TimeSpan? _hydrationElapsed;

    private readonly ConcurrentDictionary<Guid, EvidenceItem> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byContentHash = new(StringComparer.Ordinal);

    // Guards the once-per-instance hydration. Deliberately not disposed (the store is not IDisposable):
    // SemaphoreSlim only allocates a disposable WaitHandle if AvailableWaitHandle is read, and it never is
    // here — only WaitAsync/Release, which keeps cancellation working during the first read.
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    /// <param name="timeProvider">
    /// Spec 203 §1: the clock whose MONOTONIC timestamp pair measures the hydration walk. Optional and
    /// trailing so every existing construction site is untouched; <c>null</c> ⇒ <see cref="TimeProvider.System"/>.
    /// </param>
    public FileRawEvidenceStore(
        FileRawEvidenceStoreOptions options,
        ILogger<FileRawEvidenceStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public TimeSpan? HydrationElapsed => _hydrationElapsed;

    /// <remarks>
    /// Spec 206 §3: the outcome is TYPED and this method is now the collection pass's admission decision, so
    /// it hydrates first (which <see cref="AddIfNewAsync"/> used to do at the same point of the run) — the
    /// content-hash index is what makes the dedupe hold across the accrued store AND across two collectors
    /// finding the same content under different source-type paths in one run. On any failure the item is
    /// indexed NOWHERE, so a later call in the same process naturally retries. An existing final path that
    /// cannot be resolved as the SAME VALID evidence is <see cref="DurableWriteOutcome.Failed"/>, never
    /// <see cref="DurableWriteOutcome.AlreadyAvailable"/> — the durable record there is not trustworthy for
    /// this evidence, and it is never overwritten (insert-only, AD-1).
    /// </remarks>
    public async Task<DurableWriteResult> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        var path = PathFor(evidence);

        // The same immutable content is already in the hydrated accrued index — under this path or any
        // other (two collectors can land the same content under two source-type folders) — AND its file is
        // verifiably on disk. Both conditions, deliberately: an index entry can also arrive via a bare
        // AddIfNewAsync (a legacy call order), and AlreadyAvailable is a DURABILITY claim, so reporting it
        // from the in-memory index alone would be exactly the truth gap this outcome exists to close. An
        // indexed-but-not-on-disk item falls through to the write below.
        if (evidence.ContentHash is not null
            && _byContentHash.TryGetValue(evidence.ContentHash, out var indexedId)
            && _byId.TryGetValue(indexedId, out var indexed)
            && File.Exists(PathFor(indexed)))
        {
            return DurableWriteResult.AlreadyOnDisk(PathFor(indexed));
        }

        // Insert-only (AD-1): an existing final path is never overwritten. Not being in the index means
        // hydration skipped it (unreadable/conflicting) or another process wrote it after this instance
        // hydrated — resolve which by reading it back.
        if (File.Exists(path))
        {
            return await ResolveExistingPathAsync(evidence, path, ct).ConfigureAwait(false);
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
            // can never clobber it.
            IndexInsertOnly(evidence);
            return DurableWriteResult.Succeeded(path);
        }
        catch (IOException ex) when (File.Exists(path))
        {
            // Insert race: a concurrent writer won the CreateNew and created the immutable final path
            // first. Read it back to confirm it really is this evidence before reporting it durable.
            _logger.LogDebug(
                ex,
                "Raw evidence file appeared concurrently for evidence {EvidenceId} at {Path}; resolving the existing file.",
                evidence.Id,
                path);
            return await ResolveExistingPathAsync(evidence, path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A genuine disk hiccup (the final path still doesn't exist) must never crash the run. Logged at
            // Debug, deliberately (spec 206 §3): CollectionPass — the one production caller — counts every
            // Failed item and emits ONE aggregated pass-level Warning, so a per-file Warning here would turn
            // one operator signal into N+1 (the spec-195 §1 precedent).
            _logger.LogDebug(
                ex,
                "Failed to write raw evidence file for evidence {EvidenceId} at {Path}; reporting Failed.",
                evidence.Id,
                path);
            return DurableWriteResult.NotPersisted(path);
        }
    }

    /// <summary>
    /// Resolves an existing final path that the hydrated index does not hold (spec 206 §3): read it back and
    /// require it to be the SAME VALID evidence — deserializable, honestly reconstructible, and carrying this
    /// evidence's content hash. Then it is <see cref="DurableWriteOutcome.AlreadyAvailable"/> (and is indexed
    /// so later reads resolve it); anything else is <see cref="DurableWriteOutcome.Failed"/>, because the
    /// bytes at the path are not a trustworthy durable record OF THIS EVIDENCE and the insert-only rule
    /// (AD-1) forbids replacing them. Logged at Debug for the same one-operator-signal reason as the write
    /// failure path; the caller counts the loss.
    /// </summary>
    private async Task<DurableWriteResult> ResolveExistingPathAsync(
        EvidenceItem evidence, string path, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<RawEvidenceFile>(text, RadarFileStoreJson.Options);
            var existing = parsed is null ? null : ToEvidenceItem(parsed, path);
            if (existing is null || !string.Equals(existing.ContentHash, evidence.ContentHash, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Raw evidence file at {Path} exists but does not resolve as the same valid evidence "
                        + "(expected content hash '{ContentHash}'); reporting Failed for evidence {EvidenceId}.",
                    path,
                    evidence.ContentHash,
                    evidence.Id);
                return DurableWriteResult.NotPersisted(path);
            }

            // Index the ON-DISK record (the durable truth). TryAdd semantics: a concurrent insert of the
            // same content between the index probe and here simply leaves the earlier entry standing.
            TryIndexInsert(existing);
            return DurableWriteResult.AlreadyOnDisk(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(
                ex,
                "Failed to read back the existing raw evidence file at {Path}; reporting Failed for evidence {EvidenceId}.",
                path,
                evidence.Id);
            return DurableWriteResult.NotPersisted(path);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // IEvidenceRepository — the DURABLE read path (spec 142).
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <b>Index-only, by design</b> — the disk write stays with <see cref="WriteIfNewAsync"/>, which since
    /// spec 206 §3 the collection pass calls FIRST (durability is the admission decision, and that method
    /// both writes the file and indexes the item). Splitting them keeps the insert-only file semantics
    /// (AD-1) and the append-only run behaviour (AD-8) exactly as they were. Hydrates first, so "new" means
    /// new to the ACCRUED store, not merely to this process — that is what makes re-collection idempotent.
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
    /// Why <see cref="TryIndexInsert"/> refused an item. The two rejections are NOT interchangeable:
    /// <see cref="DuplicateContentHash"/> is expected in accrued history and loses nothing (the same
    /// content is already indexed), whereas <see cref="EvidenceIdConflict"/> means two files carrying
    /// DIFFERENT content claim the SAME evidence id, so the loser's content is dropped outright.
    /// </summary>
    private enum IndexInsertOutcome
    {
        Inserted,
        DuplicateContentHash,
        EvidenceIdConflict,
    }

    /// <summary>
    /// The atomic check-and-add shared by <see cref="AddIfNewAsync"/> and <see cref="WriteIfNewAsync"/>:
    /// the content-hash index enforces the unique-hash dedupe rule, and the id index preserves
    /// immutability (an existing record under the same id is never overwritten). A failed id insert rolls
    /// the hash entry back so the two indexes stay consistent. Mirrors
    /// <c>InMemoryEvidenceRepository.AddIfNewAsync</c> exactly.
    /// </summary>
    private bool IndexInsertOnly(EvidenceItem item) =>
        TryIndexInsert(item) == IndexInsertOutcome.Inserted;

    /// <summary>
    /// <see cref="IndexInsertOnly"/>, but reporting WHICH index refused — hydration needs that distinction
    /// to tell a duplication-rate figure from a data-loss figure.
    /// </summary>
    private IndexInsertOutcome TryIndexInsert(EvidenceItem item)
    {
        if (!_byContentHash.TryAdd(item.ContentHash, item.Id))
        {
            return IndexInsertOutcome.DuplicateContentHash;
        }

        if (!_byId.TryAdd(item.Id, item))
        {
            _byContentHash.TryRemove(item.ContentHash, out _);
            return IndexInsertOutcome.EvidenceIdConflict;
        }

        return IndexInsertOutcome.Inserted;
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
    /// A file rejected by the index is classified by <see cref="IndexInsertOutcome"/>: same content is a
    /// duplicate collapse (Debug, nothing lost), a same-id/different-content clash is data loss (Warning).
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

            // TWO counters, deliberately, not one "skipped" tally (spec 145): a duplicate-content collapse
            // and an unreadable file mean completely different things. The first is the DUPLICATION RATE —
            // the number this store's identity fix is measured by, and the number that says whether accrued
            // history still carries per-run copies. The second is data loss. Summing them into one figure
            // hid the first behind the second. The split is by CAUSE, not by convenience: an item rejected
            // for an evidence-id conflict has had its content dropped, so it counts as unreadable (loss),
            // never as a duplicate collapse (no loss).
            var duplicatesCollapsed = 0;
            var unreadable = 0;

            // Spec 203 §1: monotonic, never wall-clock subtraction (spec 187 §7's rule).
            var started = _timeProvider.GetTimestamp();

            if (Directory.Exists(_options.RootDirectory))
            {
                // Ordinal-sorted, NOT raw enumeration order: hydration de-dupes by ContentHash and
                // TryAdds, so when two files carry the same hash the FIRST file read wins. Duplicates
                // exist and always will: legacy files were written when the mapper minted a fresh evidence
                // Guid per run (pre-spec-145), and the same content could land under two different
                // source-type folders when two collectors found it (since spec 206 §3 the write path
                // refuses a second file for content the hydrated index already holds, so a NEW same-content
                // pair can only arise from two processes writing concurrently — the accrued ones remain).
                // Directory.EnumerateFiles has no defined order, so an unsorted walk would let the winning
                // item — and therefore the scored evidence set — vary between runs and between OSes.
                // Sorting makes the survivor a function of the path alone.
                //
                // PROVENANCE IS NOT COLLAPSED, only identity is: every contributing source's own file stays
                // on disk untouched (insert-only, AD-1). Nothing here deletes or rewrites anything.
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
                            unreadable++;
                            continue;
                        }

                        var item = ToEvidenceItem(parsed, file);
                        if (item is null)
                        {
                            // ToEvidenceItem already logged WHY the file could not be honestly
                            // reconstructed (unknown sourceType / missing required provenance field).
                            unreadable++;
                            continue;
                        }

                        switch (TryIndexInsert(item))
                        {
                            case IndexInsertOutcome.Inserted:
                                loaded++;
                                break;

                            case IndexInsertOutcome.DuplicateContentHash:
                                // The earlier (ordinal-first) file already holds this exact content, so the
                                // identity index keeps ONE canonical record while this file stays on disk
                                // with its own source attribution intact. NOTHING is lost, which is why this
                                // is Debug — and why it is counted apart from unreadable files: this number
                                // is the duplication rate.
                                duplicatesCollapsed++;
                                _logger.LogDebug(
                                    "Raw evidence file '{File}' duplicates already-indexed content hash "
                                        + "'{ContentHash}'; collapsing to the ordinal-first record (file retained).",
                                    file,
                                    item.ContentHash);
                                break;

                            case IndexInsertOutcome.EvidenceIdConflict:
                                // DIFFERENT content claiming an already-indexed evidence id — the shape a
                                // legacy file with NO `evidenceId` property takes, since an absent property
                                // leaves the non-nullable Guid at Guid.Empty and every such file then
                                // collides on that one id. (A present-but-null/blank one throws instead, and
                                // is already caught as unreadable a few lines below.) This item's
                                // content is dropped from the index entirely, so it is DATA LOSS and belongs
                                // with the unreadable tally at Warning; counting it as a duplicate collapse
                                // would both overstate the duplication rate and hide the loss at Debug.
                                unreadable++;
                                _logger.LogWarning(
                                    "Raw evidence file '{File}' declares evidence id {EvidenceId}, which is "
                                        + "already held by DIFFERENT content (this file's hash '{ContentHash}' vs "
                                        + "indexed '{IndexedContentHash}'); skipping this file.",
                                    file,
                                    item.Id,
                                    item.ContentHash,
                                    _byId.TryGetValue(item.Id, out var indexed) ? indexed.ContentHash : "(unknown)");
                                break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(ex, "Failed to read raw evidence file '{File}'; skipping.", file);
                        unreadable++;
                    }
                }
            }

            var elapsed = _timeProvider.GetElapsedTime(started);
            _hydrationElapsed = elapsed;

            _logger.LogInformation(
                "Hydrated {Loaded} raw evidence item(s) from '{Root}' "
                    + "({DuplicateFiles} duplicate-content file(s) collapsed, "
                    + "{UnreadableFiles} unreadable/conflicting file(s) skipped) in {HydrationElapsed}.",
                loaded,
                _options.RootDirectory,
                duplicatesCollapsed,
                unreadable,
                elapsed);

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
