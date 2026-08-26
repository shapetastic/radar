using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Signals;
using Radar.Domain.Signals;

namespace Radar.Infrastructure.FileSystem;

/// <summary>
/// On-disk mirror of a reviewed signal and its review record, <b>and</b> the durable
/// <see cref="ISignalRepository"/> the scoring path reads accrued history through (spec 142). Writes each
/// <see cref="Signal"/> (with its embedded <see cref="SignalReview"/>) to
/// <c>{RootDirectory}/{yyyy}/{MM}/{signalId}.json</c>, preserving provenance (evidence id, resolved
/// company id, and the embedded review whose <c>signalId</c> traces back to the signal). All file I/O
/// is confined to Infrastructure; the Application sees only <see cref="ISignalFileStore"/> /
/// <see cref="ISignalRepository"/>. Disk failures degrade gracefully (warn + return the attempted path)
/// and never crash the run.
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
public sealed class FileSignalStore : ISignalFileStore, ISignalRepository
{
    private readonly FileSignalStoreOptions _options;
    private readonly ILogger<FileSignalStore> _logger;

    // The hydration cache: every persisted signal, keyed by id. Also carries signals added in THIS process
    // (AddAsync / WriteAsync), so a write is visible to a subsequent read without touching the disk.
    private readonly ConcurrentDictionary<Guid, Signal> _byId = new();

    // Guards the once-per-instance hydration. Deliberately not disposed (the store is not IDisposable):
    // SemaphoreSlim only allocates a disposable WaitHandle if AvailableWaitHandle is read, and it never is
    // here — only WaitAsync/Release, which keeps cancellation working during a multi-second first read.
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    public FileSignalStore(
        FileSignalStoreOptions options,
        ILogger<FileSignalStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<string> WriteAsync(Signal signal, SignalReview review, CancellationToken ct)
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

        if (await GracefulFileWriter.TryWriteAllTextAsync(path, json, _logger, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Wrote signal {SignalId} to {Path}.", signal.Id, path);
        }

        // Keep the in-process index in step with the disk (upsert-by-Id, matching this store's
        // last-write-wins file semantics) so a write is immediately visible to a later repository read.
        // Deliberately does NOT hydrate: writes stay cheap, and hydration's TryAdd can never clobber this.
        _byId[signal.Id] = signal;

        return path;
    }

    public async Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
        Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc,
        DateTimeOffset knownAsOfUtc, CancellationToken ct)
    {
        // WriteAsync stores each signal date-partitioned at {RootDirectory}/{yyyy}/{MM}/{signalId}.json
        // (by ObservedAtUtc), NOT grouped by company. Rather than scan the whole tree on every
        // per-company read (scoring calls this once per company, so a full-tree scan would be
        // O(companies × totalSignalFiles) and degrade as the store grows), open only the year/month
        // directories the requested window can touch and filter those files by the persisted CompanyId.
        // Files are streamed rather than materialised into a list so cancellation stays responsive.
        if (!Directory.Exists(_options.RootDirectory))
        {
            return Array.Empty<Signal>();
        }

        var matches = new List<Signal>();
        foreach (var monthDirectory in EnumerateWindowMonthDirectories(startExclusiveUtc, endInclusiveUtc))
        {
            if (!Directory.Exists(monthDirectory))
            {
                continue;
            }

            try
            {
                // Files live directly under {yyyy}/{MM}/, so a top-directory enumeration suffices.
                foreach (var file in Directory.EnumerateFiles(monthDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<SignalFile>(text, RadarFileStoreJson.Options);
                        if (parsed is null)
                        {
                            // A JSON literal `null` deserializes to a null record — treat it as a malformed
                            // entry so operators can spot corrupted signal files.
                            _logger.LogWarning("Signal file '{File}' contained a null signal; skipping.", file);
                            continue;
                        }

                        // Approved-only + in the (startExclusive, endInclusive] window for this company. The
                        // shared boundary (AD-6): a signal exactly at endInclusiveUtc (== the current window's
                        // start) belongs to THIS previous window and is never double-counted against the
                        // current window.
                        if (parsed.CompanyId != companyId
                            || parsed.ReviewStatus != SignalReviewStatus.Approved
                            || parsed.ObservedAt <= startExclusiveUtc
                            || parsed.ObservedAt > endInclusiveUtc)
                        {
                            continue;
                        }

                        // Point-in-time honesty (spec 136): only what Radar KNEW by knownAsOfUtc — skip a
                        // signal created after the threshold (CreatedAt <= knownAsOfUtc must hold, equality
                        // included so a forward run keeps its own signals). A null CreatedAt (a file written
                        // before this field's predicate existed) is unknown → INCLUDED, preserving pre-136
                        // behaviour for that history; such history is NOT replay-honest and cannot be — the
                        // fact was never recorded.
                        if (parsed.CreatedAt is not null && parsed.CreatedAt > knownAsOfUtc)
                        {
                            continue;
                        }

                        // Reconstruct the full Signal from the persisted fields. Evidence / ScoreEvidenceLinks
                        // are intentionally NOT rehydrated: this is the activity-only previous window for
                        // velocity (Strength magnitude), NOT dropped provenance — AD-6 says it carries none.
                        matches.Add(ToSignal(parsed));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        // One unreadable/malformed signal file must not break the whole read.
                        _logger.LogWarning(ex, "Failed to read signal file '{File}'; skipping.", file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Enumeration of one month directory failed (thrown lazily during iteration); skip that
                // month rather than abandoning the whole read. OperationCanceledException is not caught
                // here, so cancellation still propagates.
                _logger.LogWarning(
                    ex,
                    "Failed to enumerate signal files in '{MonthDirectory}'; skipping that month.",
                    monthDirectory);
            }
        }

        // Collapse cross-run duplicate signals before ordering (spec 85). The same underlying signal is
        // re-minted with a fresh SignalId (and CreatedAt) on every pipeline run — WriteAsync path-keys on
        // signal.Id, so N runs leave N files for ONE signal — inflating this activity-only previous window
        // and making SignalVelocityScore depend on how many times the pipeline has run (an AD-3 violation).
        // The stable identity and the collapse both live in SignalCrossRunDedupe, shared with the durable
        // repository read below so the two can never drift (spec 142).
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
    // These serve from the hydration index (the whole accrued store, plus anything this process wrote),
    // NOT from a per-call disk scan. ReadApprovedInWindowAsync above deliberately keeps its own
    // month-scoped disk read: it answers a different question (the activity-only previous window, AD-6)
    // under semantics pinned by its own tests, and it must keep working for callers that never touch the
    // repository surface. The parts that MUST agree — the persisted record shape, the SignalFile→Signal
    // mapping (ToSignal), and the cross-run identity key — are shared, not copied.
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
        _byId[signal.Id] = signal;
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
        var collapsed = SignalCrossRunDedupe.Collapse(
            _byId.Values.Where(s => s.CompanyId == companyId), SignalCopySurvivor.EarliestKnown);

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

                        if (_byId.TryAdd(parsed.SignalId, ToSignal(parsed)))
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

            _logger.LogInformation(
                "Hydrated {Loaded} signal(s) from '{Root}' ({Skipped} unreadable file(s) skipped).",
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
    /// The single <see cref="SignalFile"/> → <see cref="Signal"/> mapping, shared by the window read and
    /// hydration so a field can never be reconstructed two different ways.
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
    /// Yields the <c>{RootDirectory}/{yyyy}/{MM}</c> partition directories that the
    /// <c>(startExclusiveUtc, endInclusiveUtc]</c> window can contain signals for — every month from the
    /// start bound's month through the end bound's month, inclusive. The start bound is exclusive, but a
    /// signal later in that same month is still in-window, so its month is still scanned. Bounding the
    /// scan to the window (typically one or two months) keeps each per-company read from touching files
    /// that cannot possibly match.
    /// </summary>
    private IEnumerable<string> EnumerateWindowMonthDirectories(
        DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc)
    {
        // Partition names come from the persisted ObservedAtUtc, so compare in UTC.
        var startUtc = startExclusiveUtc.ToUniversalTime();
        var endUtc = endInclusiveUtc.ToUniversalTime();
        if (endUtc < startUtc)
        {
            yield break;
        }

        var cursor = new DateTime(startUtc.Year, startUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(endUtc.Year, endUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= last)
        {
            yield return Path.Combine(
                _options.RootDirectory,
                cursor.ToString("yyyy", CultureInfo.InvariantCulture),
                cursor.ToString("MM", CultureInfo.InvariantCulture));
            cursor = cursor.AddMonths(1);
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
