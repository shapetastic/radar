using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Application.NewsTyping;
using Radar.Infrastructure.FileSystem;

namespace Radar.Infrastructure.NewsTyping;

/// <summary>Options for <see cref="FileNewsTypingAttemptLedger"/>: the news-typing output root (reservations live under <c>{root}/attempt-reservations/</c>).</summary>
public sealed class FileNewsTypingAttemptLedgerOptions
{
    public required string RootDirectory { get; init; }
}

/// <summary>
/// The insert-only durable PRE-CALL typing attempt ledger (spec 187 §3), on disk as
/// <c>{root}/attempt-reservations/{policy-segment}/{shard}/{reservationId}.json</c> — the policy segment is
/// LAYOUT only (<see cref="NewsTypingCohortPath"/>); the reservation's <c>CohortKey</c> FIELD stays the
/// authoritative identity. Follows <see cref="FileNewsTypingStore"/>'s mechanism exactly (lazy
/// once-per-instance thread-safe hydration into an id index, deterministic ordinal enumeration,
/// <c>TryAdd</c>-only indexing, <see cref="FileMode.CreateNew"/> writes, and a malformed file logged and
/// skipped rather than thrown) rather than re-inventing one.
///
/// <para>
/// <b>The layout carries NO WALL CLOCK, and that is load-bearing rather than cosmetic.</b> The outcome
/// store partitions on <c>{yyyy}/{MM}</c> because a typing record is an OBSERVATION, filed by when it was
/// made. A reservation is a LOCK, and a lock whose directory depends on a clock is not one: two genuinely
/// concurrent processes straddling a UTC month boundary would create <c>…/2026/08/{id}.json</c> and
/// <c>…/2026/09/{id}.json</c> and BOTH would win <see cref="FileMode.CreateNew"/> — mutual exclusion on the
/// file NAME but not on the path. So the reservation's FULL PATH is a pure function of its deterministic
/// identity (AD-3): the policy segment, then a fan-out <c>{shard}</c> that is simply the first two hex
/// characters of the reservation id (256 buckets, purely to keep one directory from accruing every
/// reservation ever made — see <see cref="ShardFor"/>), then the id. Two racing processes therefore contend
/// for exactly one file however far apart their clocks are. <c>ReservedAtUtc</c> remains ON the record as
/// provenance; it simply reaches no path segment.
/// </para>
/// <para>
/// <b>The ONE behavioural difference from the outcome store, and it is the whole point.</b>
/// <see cref="FileNewsTypingStore.WriteAsync"/> treats an already-existing file as a benign re-run dedupe
/// and returns <c>true</c>. Here an already-existing file means <b>this caller LOST the race</b> for the
/// ordinal and must return <c>false</c>: the return value is not "is it recorded" but "am I the one process
/// permitted to spend a hosted provider call on this attempt". <see cref="FileMode.CreateNew"/> is what
/// makes that mutually exclusive across processes; the in-memory index makes it mutually exclusive within
/// one. A read or create FAILURE is likewise reported as <c>false</c> — "cannot tell" must fail towards NOT
/// calling the provider, because an unrecorded call is exactly the leak this ledger exists to close.
/// </para>
/// <para>
/// <b>KNOWN residue, recorded rather than fixed — a truncated reservation file wedges its ordinal.</b> A
/// reservation whose file was created but whose JSON never landed (a crash or a full disk between the
/// <see cref="FileMode.CreateNew"/> and the flush) hydrates as UNREADABLE: it is logged and skipped, so it
/// is not counted as occupancy, yet the file still exists and so still blocks <c>CreateNew</c> at that
/// ordinal. The generator therefore re-selects that observation every pass, is refused every pass, and the
/// attempt never advances — indefinitely, until the empty file is removed by hand. Because the path is
/// clock-free the wedge genuinely persists: it does NOT quietly resolve itself at a month rollover the way
/// a date-partitioned layout would have (which would have been a correctness hole wearing a self-heal
/// costume — the same rollover would have let two racers both call the provider). This is the
/// CONSERVATIVE direction and can never overspend the provider budget: a wedged ordinal only ever suppresses
/// calls. It is visible, not silent — each refusal increments the pass's refused-reservation count, which
/// degrades that company's typing completeness to <c>Failed</c> and is surfaced in the aggregated per-cohort
/// warning. Fixing it would mean either trusting an unreadable file's PATH as identity (re-admitting the
/// filesystem-metadata reasoning spec 186 §3 deleted) or deleting files during hydration (a write on a read
/// path, in an insert-only store); neither is worth a bounded, self-announcing, fail-closed residue.
/// </para>
/// Read-side and shadow: nothing here is a scoring input or a fingerprint input.
/// </summary>
public sealed class FileNewsTypingAttemptLedger : INewsTypingAttemptLedger
{
    private const string ReservationsFolder = "attempt-reservations";

    private readonly FileNewsTypingAttemptLedgerOptions _options;
    private readonly ILogger<FileNewsTypingAttemptLedger> _logger;
    private readonly ConcurrentDictionary<Guid, NewsTypingAttemptReservation> _byId = new();
    private readonly SemaphoreSlim _hydrationGate = new(1, 1);
    private volatile bool _hydrated;

    public FileNewsTypingAttemptLedger(
        FileNewsTypingAttemptLedgerOptions options, ILogger<FileNewsTypingAttemptLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public async Task<bool> TryReserveAsync(NewsTypingAttemptReservation reservation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        await EnsureHydratedAsync(ct).ConfigureAwait(false);

        if (!_byId.TryAdd(reservation.ReservationId, reservation))
        {
            // Already claimed — by an earlier pass whose reservation hydrated, or by a concurrent caller in
            // this process. Either way THIS caller may not call the provider.
            _logger.LogDebug(
                "News-typing attempt reservation {ReservationId} (ordinal {Ordinal}) is already claimed.",
                reservation.ReservationId,
                reservation.AttemptOrdinal);
            return false;
        }

        // Every segment below is derived from the reservation's deterministic identity — no clock (AD-3),
        // so racing processes contend for exactly one file regardless of when each thinks it is.
        var path = Path.Combine(
            _options.RootDirectory,
            ReservationsFolder,
            NewsTypingCohortPath.PolicySegment(reservation.Provider, reservation.ModelId),
            ShardFor(reservation.ReservationId),
            reservation.ReservationId.ToString("D") + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var streamOptions = new FileStreamOptions
            {
                // ATOMIC create-new: two processes racing for one ordinal cannot both succeed. This is the
                // cross-process half of the mutual exclusion; the TryAdd above is the in-process half.
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            await using var stream = new FileStream(path, streamOptions);
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(reservation, RadarFileStoreJson.Options));
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Either another PROCESS won the create-new race, or the write genuinely failed. Both must read
            // as "not my attempt": the index entry is withdrawn so a later pass re-derives occupancy from
            // disk, and no hosted call is made.
            _byId.TryRemove(reservation.ReservationId, out _);
            _logger.LogWarning(
                ex,
                "Could not claim news-typing attempt reservation {ReservationId} (ordinal {Ordinal}) at "
                    + "{Path}; no hosted call will be made for it this pass.",
                reservation.ReservationId,
                reservation.AttemptOrdinal,
                path);
            return false;
        }
    }

    /// <summary>
    /// The fan-out directory for one reservation: the first two hex characters of its id
    /// (<c>Guid.ToString("N")</c>, which is lowercase hex by definition). Purely a bucket so no single
    /// directory accrues every reservation ever made — it carries no meaning, is never parsed back, and
    /// deliberately consults NOTHING but the deterministic id, so the full reservation path stays a pure
    /// function of identity (AD-3). Hydration enumerates recursively and so is indifferent to it.
    /// </summary>
    private static string ShardFor(Guid reservationId) => reservationId.ToString("N")[..2];

    public async Task<IReadOnlyList<NewsTypingAttemptReservation>> GetAllAsync(CancellationToken ct)
    {
        await EnsureHydratedAsync(ct).ConfigureAwait(false);
        return [.. _byId.Values.OrderBy(r => r.ReservedAtUtc).ThenBy(r => r.ReservationId)];
    }

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
            var unreadable = 0;
            var root = Path.Combine(_options.RootDirectory, ReservationsFolder);
            if (Directory.Exists(root))
            {
                List<string> files;
                try
                {
                    files = Directory
                        .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                        .Order(StringComparer.Ordinal)
                        .ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        ex, "Failed to enumerate news-typing attempt reservations under '{Root}'.", root);
                    files = [];
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var parsed = JsonSerializer.Deserialize<NewsTypingAttemptReservation>(
                            text, RadarFileStoreJson.Options);
                        if (parsed is null
                            || parsed.ReservationId == Guid.Empty
                            || string.IsNullOrEmpty(parsed.CohortKey)
                            || string.IsNullOrEmpty(parsed.PayloadHash)
                            || parsed.AttemptOrdinal < 1)
                        {
                            _logger.LogWarning(
                                "News-typing attempt reservation '{File}' is missing required identity "
                                    + "fields; skipping.",
                                file);
                            unreadable++;
                            continue;
                        }

                        if (_byId.TryAdd(parsed.ReservationId, parsed))
                        {
                            loaded++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _logger.LogWarning(
                            ex, "Failed to read news-typing attempt reservation '{File}'; skipping.", file);
                        unreadable++;
                    }
                }
            }

            _logger.LogInformation(
                "Hydrated {Loaded} news-typing attempt reservation(s) from '{Root}' ({Unreadable} "
                    + "unreadable skipped).",
                loaded,
                root,
                unreadable);
            _hydrated = true;
        }
        finally
        {
            _hydrationGate.Release();
        }
    }
}
