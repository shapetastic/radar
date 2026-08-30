using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Signals;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk mirror of a reviewed signal and its review record, <b>and</b> the durable
/// <see cref="ISignalRepository"/> the scoring path reads accrued history through (spec 142). Writes each
/// <see cref="Signal"/> (with its embedded <see cref="SignalReview"/>) to
/// <c>{RootDirectory}/{yyyy}/{MM}/{signalId}.json</c>, preserving provenance (evidence id, resolved
/// company id, and the embedded review whose <c>signalId</c> traces back to the signal). All file I/O
/// is confined to Infrastructure; the Application sees only <see cref="ISignalFileStore"/> /
/// <see cref="ISignalRepository"/>. Disk failures degrade gracefully (return the attempted path, marked
/// <see cref="DurableWriteOutcome.Failed"/>) and never crash the run — but they are no longer SILENT:
/// spec 193 §1 gives the caller a typed outcome so a signal that never reached disk cannot be counted as
/// stored, and spec 195 §1 moves the FAILURE LOG to that caller
/// (<see cref="GracefulFileWriteFailureLogging.CallerAggregates"/>) so <c>CollectionPass</c>'s one
/// aggregated Warning replaces the per-file Warnings instead of being added to them.
/// <para>
/// <b>"Aggregated by <c>CollectionPass</c>" does not mean CollectionPass is the only writer.</b> The
/// second <see cref="ISignalFileStore.WriteAsync"/> consumer is
/// <c>Radar.Application.News.NewsJudgmentSignalMaterializer</c> (spec 194 §1.2), and it reports its own
/// failures: one per-signal Warning naming the company and judgment, PLUS a per-pass aggregate
/// (<c>WriteFailed</c> on its materialization summary). So nothing goes silent on that path either — which
/// is the standing condition for this mode, not an accident of who happens to call.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>THE REPOSITORY IS THE FILE STORE (spec 142's recorded reconciliation choice).</b> Before this slice
/// there were two disconnected abstractions over the same facts: <see cref="ISignalFileStore"/> owned the
/// durable format, while <see cref="ISignalRepository"/> resolved to an in-memory singleton that started
/// EMPTY every process — so scoring had never once read accrued history. The options were (a) a third type
/// wrapping the file store, or (b) this: the file store additionally implements the repository. (b) was
/// chosen because it is the only one with no second copy of the persisted shape — one record definition,
/// one deserializer, one set of skip-don't-throw rules, one hydration cache — and because a wrapper would
/// have to re-read what the store already knows how to read. Composition keeps ONE instance and exposes it
/// under both interfaces (see <c>AddDurableRadarSignalHistory</c>). The in-memory implementations stay, for
/// tests.
/// </para>
/// <para>
/// <b>Hydration</b> is lazy (never in the constructor), once per instance, and thread-safe. The first read
/// walks the whole tree once and indexes every persisted signal by id; writes update the index directly, so
/// a write is immediately visible to a later read in the same process. Hydration only ever
/// <c>TryAdd</c>s, so a signal this process wrote always wins over its own on-disk copy. Only the
/// <see cref="Signal"/> fields are retained — the embedded <see cref="SignalReview"/> is dropped, which is
/// what keeps a ~50k-file store's footprint modest.
/// </para>
/// <para>
/// <b>Overwrite-allowed (upsert-by-Id, last-write-wins).</b> This deliberately DIFFERS from the
/// insert-only <see cref="FileRawEvidenceStore"/>: AD-1 immutability governs <i>evidence only</i>.
/// Signals are upsert-by-Id, so an existing file for the same signal id is overwritten rather than
/// skipped. This is intentional — do not re-flag it as an AD-1 violation.
/// </para>
/// </remarks>
public sealed class FileSignalStore : ISignalFileStore, ISignalRepository, IHydrationTelemetry
{
    private readonly FileSignalStoreOptions _options;
    private readonly ILogger<FileSignalStore> _logger;
    private readonly TimeProvider _timeProvider;

    // The hydration cache: every persisted signal, keyed by id. Also carries signals added in THIS process
    // (AddAsync / WriteAsync), so a write is visible to a subsequent read without touching the disk.
    private readonly ConcurrentDictionary<Guid, Signal> _byId = new();

    // Spec 203 §3: the SAME signals, bucketed by company id, so a per-company read filters the company's own
    // signals instead of scanning the whole 64k index. Maintained at EVERY _byId mutation site (hydration
    // TryAdd, AddAsync, WriteAsync) through IndexByCompany, never anywhere else, so the two views cannot
    // drift. A signal whose CompanyId is null is held in _byId only: no company read could ever have matched
    // it (the previous `s.CompanyId == companyId` compared a Guid? against a Guid), so it has no bucket.
    // Guarded by _byCompanyGate: writes are rare and reads copy the bucket under the lock, which keeps the
    // per-company view deterministic without a second concurrent structure.
    private readonly Dictionary<Guid, Dictionary<Guid, Signal>> _byCompany = [];
    private readonly object _byCompanyGate = new();

    // Spec 203 §1: the measured hydration walk (monotonic). Null until this instance hydrates.
    private TimeSpan? _hydrationElapsed;

    // Guards the once-per-instance hydration. Deliberately not disposed (the store is not IDisposable):
    // SemaphoreSlim only allocates a disposable WaitHandle if AvailableWaitHandle is read, and it never is
    // here — only WaitAsync/Release, which keeps cancellation working during a multi-second first read.
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    /// <param name="timeProvider">
    /// Spec 203 §1: the clock whose MONOTONIC timestamp pair measures the hydration walk. Optional and
    /// trailing so every existing construction site is untouched; <c>null</c> ⇒ <see cref="TimeProvider.System"/>.
    /// </param>
    public FileSignalStore(
        FileSignalStoreOptions options,
        ILogger<FileSignalStore> logger,
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

    public async Task<DurableWriteResult> WriteAsync(Signal signal, SignalReview review, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(review);

        // Provenance guard: the embedded review must belong to this signal. Persisting a mismatched
        // pair would write an internally inconsistent file and silently break the review→signal trace.
        if (review.SignalId != signal.Id)
        {
            throw new ArgumentException(
                $"Review {review.Id} targets signal {review.SignalId}, not signal {signal.Id}; refusing to persist a mismatched pair.",
                nameof(review));
        }

        var observedUtc = signal.ObservedAtUtc.ToUniversalTime();
        var path = Path.Combine(
            _options.RootDirectory,
            observedUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            observedUtc.ToString("MM", CultureInfo.InvariantCulture),
            signal.Id + ".json");

        var json = Serialize(signal, review);

        // Spec 195 §1: CallerAggregates, because CollectionPass emits ONE aggregated
        // "{SignalsNotPersisted} signal(s) could NOT be durably persisted" Warning for exactly these
        // failures. Without this the batch path logged N per-file Warnings PLUS that aggregate.
        var written = await GracefulFileWriter
            .TryWriteAllTextAsync(
                path,
                json,
                _logger,
                ct,
                encoding: null,
                failureLogging: GracefulFileWriteFailureLogging.CallerAggregates)
            .ConfigureAwait(false);
        if (written)
        {
            _logger.LogInformation("Wrote signal {SignalId} to {Path}.", signal.Id, path);
        }

        // Keep the in-process index in step with the disk (upsert-by-Id, matching this store's
        // last-write-wins file semantics) so a write is immediately visible to a later repository read.
        // Deliberately does NOT hydrate: writes stay cheap, and hydration's TryAdd can never clobber this.
        //
        // SPEC 193 §1: this happens on BOTH outcomes, deliberately — the current run must still complete on
        // what it has. What changes is the CLAIM: a failed write returns Failed, so the pipeline counts the
        // signal as not-persisted instead of silently reporting a path that holds nothing. The next run's
        // accrued-history read will not see it, and that fact is now recorded rather than discarded.
        IndexUpsert(signal);

        return DurableWriteResult.From(path, written);
    }

    /// <summary>
    /// The activity-only previous/velocity window read (AD-6), served from the hydration index since spec 203
    /// §2. The predicate and its order are EXACTLY the pre-203 disk scan's: Approved only, then
    /// <c>ObservedAtUtc</c> in <c>(startExclusive, endInclusive]</c>, then the spec-136 known-at rule
    /// <c>CreatedAtUtc &lt;= knownAsOfUtc</c>, then the spec-85 cross-run collapse (lowest id) and the
    /// deterministic <c>ObservedAtUtc</c>/<c>Id</c> ordering. The existing tests pin those semantics unmodified.
    /// <para>
    /// <b>The ONE semantic edge, stated honestly.</b> The disk scan read a legacy file with no <c>createdAt</c>
    /// as "knowledge date unknown ⇒ INCLUDED unconditionally". The index holds that file through the same
    /// <see cref="ToSignal"/> mapping hydration always used, which sets <c>CreatedAtUtc = ObservedAt</c>, so
    /// here the predicate on such a file is <c>ObservedAt &lt;= knownAsOfUtc</c>. The two readings differ only
    /// when <c>knownAsOfUtc &lt; endInclusiveUtc</c> — a known-at instant EARLIER than the window it asks about
    /// — which no production caller does: <c>ScoringEngine</c> passes <c>knownAsOf = windowEndUtc</c>, which is
    /// ≥ <c>endInclusive = windowStartUtc</c>. Under that contract (asserted by the spec-203 equivalence test)
    /// the two implementations are element-for-element identical.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
        Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc,
        DateTimeOffset knownAsOfUtc, CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Approved-only + in the (startExclusive, endInclusive] window for this company. The shared boundary
        // (AD-6): a signal exactly at endInclusiveUtc (== the current window's start) belongs to THIS previous
        // window and is never double-counted against the current window.
        //
        // Point-in-time honesty (spec 136): only what Radar KNEW by knownAsOfUtc — skip a signal created after
        // the threshold (CreatedAt <= knownAsOfUtc must hold, equality included so a forward run keeps its own
        // signals).
        var matches = CompanySignals(companyId)
            .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
            .Where(s => s.ObservedAtUtc > startExclusiveUtc && s.ObservedAtUtc <= endInclusiveUtc)
            .Where(s => s.CreatedAtUtc <= knownAsOfUtc);

        // Collapse cross-run duplicate signals before ordering (spec 85). The same underlying signal is
        // re-minted with a fresh SignalId (and CreatedAt) on every pipeline run — WriteAsync path-keys on
        // signal.Id, so N runs leave N files for ONE signal — inflating this activity-only previous window
        // and making SignalVelocityScore depend on how many times the pipeline has run (an AD-3 violation).
        // The stable identity and the collapse both live in SignalCrossRunDedupe, shared with the durable
        // repository reads below so the two can never drift (spec 142).
        //
        // Survivor rule: LOWEST SignalId. Correct HERE — and deliberately different from the repository
        // read's EarliestKnown — because this read has ALREADY applied the known-at predicate above, so
        // every copy that reaches the collapse is equally "known by knownAsOfUtc" and the choice cannot
        // change which signals are visible. All copies carry identical activity fields (Strength), so it
        // cannot change the velocity result either; lowest SignalId is simply the simplest reproducible
        // total order. Grouping is order-independent, so the survivor is the same every read.
        var deduped = SignalCrossRunDedupe.Collapse(matches, SignalCopySurvivor.LowestId);

        // Deterministic order (AD-3): ObservedAtUtc then Id.
        return deduped
            .OrderBy(s => s.ObservedAtUtc)
            .ThenBy(s => s.Id)
            .ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // ISignalRepository — the DURABLE read path (spec 142).
    //
    // EVERY read — these AND ReadApprovedInWindowAsync above — serves from the hydration index (the whole
    // accrued store, plus anything this process wrote), never from a per-call disk scan. Until spec 203 the
    // window read kept its own month-scoped disk scan on the argument that it "answers a different question
    // under semantics pinned by its own tests" — which was an argument about the FILTER (window, known-at,
    // Approved, LowestId survivor), not about the SOURCE: the index holds byte-identical records through
    // the same ToSignal mapping the scan used. Measured on the 2026-08-29 baseline that scan cost ~12k file
    // reads per call × 1,034 calls. The filter semantics are unchanged and still pinned by the same tests;
    // the per-file skip-don't-throw rule now lives ONLY in hydration, which always had it. A caller that
    // never touched the repository surface now pays the one-time hydration on its first call instead of a
    // scan on every call (every production caller already hydrates via AddDurableRadarSignalHistory).
    // ---------------------------------------------------------------------------------------------

    /// <remarks>
    /// <b>Index-only, by design.</b> <see cref="ISignalRepository.AddAsync"/> carries no
    /// <see cref="SignalReview"/>, and the durable format REQUIRES one (<see cref="WriteAsync"/> refuses to
    /// persist a signal whose embedded review does not trace back to it). Writing a review-less file here
    /// would therefore either break that provenance guard or invent a review — so it does neither.
    /// Durability continues to come from the pipeline's existing
    /// <see cref="ISignalFileStore.WriteAsync"/> call, which <c>RadarPipelineRunner</c> makes immediately
    /// after this one. Append-only (AD-8) and the review→signal provenance guard are both preserved
    /// unchanged.
    /// </remarks>
    public Task AddAsync(Signal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ct.ThrowIfCancellationRequested();

        // Upsert by Id (last-write-wins), matching the interface contract and WriteAsync's file semantics.
        IndexUpsert(signal);
        return Task.CompletedTask;
    }

    public async Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);
        _byId.TryGetValue(id, out var signal);
        return signal;
    }

    public async Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Cross-run duplicate collapse is REQUIRED here, not tidiness: the in-memory repository this
        // replaces only ever held ONE run, so it never saw duplicates; the accrued store holds a copy per
        // run of the same underlying signal and, un-collapsed, they would inflate the current scoring
        // window and therefore the score itself.
        //
        // Survivor rule: EARLIEST CreatedAtUtc, then lowest Id. The spec-136 known-at predicate
        // (CreatedAtUtc <= windowEndUtc) is applied by ScoringEngine AFTER this read, so keeping the
        // earliest-known copy is the only choice under which "was this signal known by T?" gets the honest
        // answer. Keeping a later-created copy would silently hide, from a replay at T, a signal Radar
        // demonstrably knew about at T.
        //
        // Spec 203 §3: over the company's OWN bucket rather than the whole index — the same set the previous
        // `_byId.Values.Where(s => s.CompanyId == companyId)` selected, O(company's signals) instead of O(all).
        var collapsed = SignalCrossRunDedupe.Collapse(
            CompanySignals(companyId), SignalCopySurvivor.EarliestKnown);

        // Deterministic order (AD-3), identical to InMemorySignalRepository's.
        return [.. collapsed.OrderBy(s => s.ObservedAtUtc).ThenBy(s => s.Id)];
    }

    public async Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        // Window bounds are inclusive on ObservedAtUtc (matching InMemorySignalRepository). Collapsed for
        // the same reason as GetByCompanyAsync, and with the same survivor rule so one signal has one
        // identity whichever read surfaced it — otherwise the weekly report's needs-review section would
        // list one signal once per run that ever re-extracted it.
        var collapsed = SignalCrossRunDedupe.Collapse(
            _byId.Values.Where(s => s.ObservedAtUtc >= startUtc && s.ObservedAtUtc <= endUtc),
            SignalCopySurvivor.EarliestKnown);

        return [.. collapsed.OrderBy(s => s.ObservedAtUtc).ThenBy(s => s.Id)];
    }

    /// <summary>
    /// Loads every persisted signal into the in-memory index, exactly once per instance. Lazy (never in
    /// the constructor, so composing the graph costs nothing) and thread-safe: concurrent first readers
    /// queue on the gate and only one walks the tree. Per-file failures are logged and SKIPPED, never
    /// thrown — one corrupt file out of ~50k must not make the whole accrued history unreadable — while
    /// <see cref="OperationCanceledException"/> still propagates. Entries are <c>TryAdd</c>ed, so a signal
    /// this process already wrote always wins over its own on-disk copy.
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

            // Spec 203 §1: monotonic, never wall-clock subtraction (spec 187 §7's rule).
            var started = _timeProvider.GetTimestamp();

            if (Directory.Exists(_options.RootDirectory))
            {
                foreach (var file in EnumerateSignalFiles())
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<SignalFile>(text, RadarFileStoreJson.Options);
                        if (parsed is null)
                        {
                            _logger.LogWarning("Signal file '{File}' contained a null signal; skipping.", file);
                            skipped++;
                            continue;
                        }

                        if (IndexTryAdd(ToSignal(parsed)))
                        {
                            loaded++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(ex, "Failed to read signal file '{File}'; skipping.", file);
                        skipped++;
                    }
                }
            }

            var elapsed = _timeProvider.GetElapsedTime(started);
            _hydrationElapsed = elapsed;

            _logger.LogInformation(
                "Hydrated {Loaded} signal(s) from '{Root}' ({Skipped} unreadable file(s) skipped) in "
                    + "{HydrationElapsed}.",
                loaded,
                _options.RootDirectory,
                skipped,
                elapsed);

            _hydrated = true;
        }
        finally
        {
            _hydrationGate.Release();
        }
    }

    /// <summary>
    /// Enumerates every <c>*.json</c> under the root. Enumeration failures (a directory that vanished or
    /// is unreadable) degrade to "no more files" rather than aborting hydration; the count logged by
    /// <see cref="EnsureHydratedAsync"/> then reflects what was actually loaded.
    /// </summary>
    private IEnumerable<string> EnumerateSignalFiles()
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
                ex, "Failed to enumerate signal files under '{Root}'; hydrating nothing.", _options.RootDirectory);
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
                        "Failed while enumerating signal files under '{Root}'; stopping enumeration early.",
                        _options.RootDirectory);
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    /// <summary>
    /// The single <see cref="SignalFile"/> → <see cref="Signal"/> mapping. Since spec 203 §2 hydration is
    /// the ONLY deserializing read (every query serves from the index it builds), so a field is
    /// reconstructed exactly one way by construction.
    /// </summary>
    private static Signal ToSignal(SignalFile parsed) => new(
        Id: parsed.SignalId,
        EvidenceId: parsed.EvidenceId,
        CompanyId: parsed.CompanyId,
        CompanyMention: parsed.CompanyMention,
        Type: parsed.Type,
        Direction: parsed.Direction,
        Strength: parsed.Strength,
        Novelty: parsed.Novelty,
        Confidence: parsed.Confidence,
        SupportingExcerpt: parsed.SupportingExcerpt,
        Reason: parsed.Reason,
        ReviewStatus: parsed.ReviewStatus,
        ObservedAtUtc: parsed.ObservedAt,
        // A legacy file without createdAt maps to ObservedAt — the earliest honest stand-in (the event
        // date), never a fabricated knowledge date. Under the spec-136 known-at predicate this makes such
        // a signal "known as early as it was observed", which is exactly the pre-136 include-it behaviour
        // the window read documents; that history is NOT replay-honest and cannot be — the fact was never
        // recorded.
        CreatedAtUtc: parsed.CreatedAt ?? parsed.ObservedAt,
        // Spec 191: absent on every pre-191 file ⇒ null ⇒ NOT RECORDED.
        MetadataJson: parsed.MetadataJson);

    /// <summary>
    /// Spec 203 §3: the company's signals as a point-in-time COPY of its bucket (taken under the gate, so a
    /// concurrent same-process write can never invalidate an enumeration in progress). Empty for a company
    /// with no bucket. Order is irrelevant: every consumer collapses and then sorts deterministically.
    /// </summary>
    private IReadOnlyList<Signal> CompanySignals(Guid companyId)
    {
        lock (_byCompanyGate)
        {
            return _byCompany.TryGetValue(companyId, out var bucket)
                ? [.. bucket.Values]
                : [];
        }
    }

    /// <summary>
    /// The ONE upsert-by-Id path for both same-process writers (<see cref="WriteAsync"/> and
    /// <see cref="AddAsync"/>): updates <c>_byId</c> AND the company index together, so the two views cannot
    /// drift. The previous entry under the same id (if any) is looked up FIRST so a re-write under a
    /// different <c>CompanyId</c> leaves the old company's bucket.
    /// </summary>
    private void IndexUpsert(Signal signal)
    {
        lock (_byCompanyGate)
        {
            _byId.TryGetValue(signal.Id, out var previous);
            _byId[signal.Id] = signal;
            IndexByCompany(signal, previous);
        }
    }

    /// <summary>
    /// The ONE insert-if-absent path (hydration): <c>_byId.TryAdd</c> AND the company index mutate under
    /// the SAME gate, so a same-process <see cref="IndexUpsert"/> can never interleave between the two and
    /// leave <c>_byId</c> holding the written copy while the bucket holds the hydrated one. Only a WINNING
    /// TryAdd reaches the bucket, so a signal this process wrote keeps both of its entries.
    /// </summary>
    private bool IndexTryAdd(Signal signal)
    {
        lock (_byCompanyGate)
        {
            if (!_byId.TryAdd(signal.Id, signal))
            {
                return false;
            }

            IndexByCompany(signal, previous: null);
            return true;
        }
    }

    /// <summary>
    /// Places <paramref name="signal"/> in its company bucket, removing <paramref name="previous"/> (the
    /// entry this id held before, or null) from ITS bucket when the company changed. A null
    /// <c>CompanyId</c> is indexed under no company (see the field comment). Takes the gate itself (the lock
    /// is re-entrant, so <see cref="IndexUpsert"/> may already hold it) so both the hydration path and the
    /// same-process writers are safe regardless of which one calls.
    /// </summary>
    private void IndexByCompany(Signal signal, Signal? previous)
    {
        lock (_byCompanyGate)
        {
            if (previous?.CompanyId is { } oldCompany
                && oldCompany != signal.CompanyId
                && _byCompany.TryGetValue(oldCompany, out var oldBucket))
            {
                oldBucket.Remove(signal.Id);
                if (oldBucket.Count == 0)
                {
                    _byCompany.Remove(oldCompany);
                }
            }

            if (signal.CompanyId is not { } company)
            {
                return;
            }

            if (!_byCompany.TryGetValue(company, out var bucket))
            {
                bucket = [];
                _byCompany[company] = bucket;
            }

            bucket[signal.Id] = signal;
        }
    }

    private static string Serialize(Signal signal, SignalReview review)
    {
        var file = new SignalFile(
            SignalId: signal.Id,
            EvidenceId: signal.EvidenceId,
            CompanyId: signal.CompanyId,
            CompanyMention: signal.CompanyMention,
            Type: signal.Type,
            Direction: signal.Direction,
            Strength: signal.Strength,
            Novelty: signal.Novelty,
            Confidence: signal.Confidence,
            SupportingExcerpt: signal.SupportingExcerpt,
            Reason: signal.Reason,
            ReviewStatus: signal.ReviewStatus,
            ObservedAt: signal.ObservedAtUtc,
            CreatedAt: signal.CreatedAtUtc,
            Review: new SignalReviewFile(
                ReviewId: review.Id,
                SignalId: review.SignalId,
                ReviewerName: review.ReviewerName,
                Decision: review.Decision,
                Summary: review.Summary,
                IssuesJson: review.IssuesJson,
                ReviewedAt: review.ReviewedAtUtc),
            MetadataJson: signal.MetadataJson);

        return JsonSerializer.Serialize(file, RadarFileStoreJson.Options);
    }

    /// <summary>
    /// The persisted signal shape. Property names render camelCase via the serializer options
    /// (<c>signalId</c>, <c>evidenceId</c>, …); enums render as their string names. Carries the
    /// provenance fields (<c>evidenceId</c>, nullable <c>companyId</c>) and the embedded review.
    /// <c>CreatedAt</c> is nullable ON READ only (spec 136): a legacy file lacking the property
    /// deserializes to an explicit null (unknown knowledge date) rather than silently to MinValue;
    /// the write side always supplies a real value, so serialized output is unchanged.
    /// </summary>
    private sealed record SignalFile(
        Guid SignalId,
        Guid EvidenceId,
        Guid? CompanyId,
        string CompanyMention,
        SignalType Type,
        SignalDirection Direction,
        int Strength,
        int Novelty,
        decimal Confidence,
        string SupportingExcerpt,
        string Reason,
        SignalReviewStatus ReviewStatus,
        DateTimeOffset ObservedAt,
        DateTimeOffset? CreatedAt,
        SignalReviewFile Review,
        // Spec 191: the signal provenance envelope. TRAILING and NULLABLE, and OMITTED WHEN NULL
        // (JsonIgnoreCondition.WhenWritingNull) so every already-written file and every signal that carries
        // no metadata serializes byte-identically to pre-191 output. On read, absent ⇒ null ⇒ NOT RECORDED —
        // never an empty bag (the spec-142 EvidenceQuality / spec-148 EffectiveScoringConfig.Window
        // precedent).
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? MetadataJson = null);

    /// <summary>
    /// The embedded review shape. Its <c>signalId</c> traces back to the parent signal (provenance).
    /// </summary>
    private sealed record SignalReviewFile(
        Guid ReviewId,
        Guid SignalId,
        string ReviewerName,
        SignalReviewDecision Decision,
        string Summary,
        string? IssuesJson,
        DateTimeOffset ReviewedAt);
}
