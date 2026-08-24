using Radar.Application.Identity;

namespace Radar.Application.NewsTyping;

/// <summary>
/// One durable PRE-CALL claim on a hosted typing attempt (spec 187 §3).
///
/// <para>
/// <b>Why this exists, and what it supersedes.</b> Spec 186 §2 bounded typing retries by DERIVING the
/// attempt count from the insert-only outcome records, under an explicit "no new store, no side index"
/// constraint. That cannot strictly bound HOSTED CALLS, because the call happens BEFORE the outcome is
/// written: a process crash, a cancellation, or an <see cref="INewsTypingStore.WriteAsync"/> that returns
/// <c>false</c> consumes a provider call while advancing the derived count by nothing — so the same
/// observation re-enters selection at the same ordinal on the next run, forever. Spec 187 §3 therefore
/// EXPLICITLY supersedes that constraint: a durable fact recorded before the call is the only thing that
/// can make "at most <c>MaxTypingAttempts</c> calls" true rather than merely intended.
/// </para>
/// <para>
/// <b>Identity is over the INPUT and the ordinal, never the run.</b>
/// <c>(cohortKey, observationId, payloadHash, attemptOrdinal)</c> — deliberately NOT the run id, because
/// the whole point is that two different processes (or two invocations of the same one, run-scoped or
/// standalone) racing for the SAME attempt must collide on the SAME file name and exactly one must win.
/// Folding the run id would give every process its own private ordinal namespace and re-open the unbounded
/// budget from the other side. <see cref="RunId"/> is carried as PROVENANCE only.
/// </para>
/// Read-side and shadow: nothing here is a scoring input, a snapshot field, or a fingerprint input.
/// </summary>
public sealed record NewsTypingAttemptReservation(
    string SchemaVersion,
    Guid ReservationId,
    string CohortKey,
    Guid ObservationId,
    string PayloadHash,
    int AttemptOrdinal,
    Guid? RunId,
    string Provider,
    string ModelId,
    DateTimeOffset ReservedAtUtc)
{
    /// <summary>The attempt-ledger schema version stamped on every reservation.</summary>
    public const string CurrentSchemaVersion = "news-typing-attempt-reservation-v1";

    /// <summary>
    /// The deterministic reservation identity: cohort + observation + payload hash + one-based attempt
    /// ordinal. Pure (no clock, no randomness — AD-3) and routed through the shared
    /// <see cref="DeterministicGuid"/> rather than a second hash idiom, so two processes computing the same
    /// ordinal compute the same <see cref="Guid"/> and therefore the same durable file path.
    /// </summary>
    public static Guid IdentityFor(
        string cohortKey, Guid observationId, string payloadHash, int attemptOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptOrdinal, 1);

        return DeterministicGuid.FromCanonicalString(
            FormattableString.Invariant(
                $"radar:news-typing-attempt:{cohortKey}:{observationId:D}:{payloadHash}:{attemptOrdinal}"));
    }

    /// <summary>
    /// Composes the reservation for one attempt. <paramref name="reservedAtUtc"/> is observational
    /// provenance only — it enters no identity (AD-3).
    /// </summary>
    public static NewsTypingAttemptReservation For(
        string cohortKey,
        Guid observationId,
        string payloadHash,
        int attemptOrdinal,
        Guid? runId,
        string provider,
        string modelId,
        DateTimeOffset reservedAtUtc) => new(
        SchemaVersion: CurrentSchemaVersion,
        ReservationId: IdentityFor(cohortKey, observationId, payloadHash, attemptOrdinal),
        CohortKey: cohortKey,
        ObservationId: observationId,
        PayloadHash: payloadHash,
        AttemptOrdinal: attemptOrdinal,
        RunId: runId,
        Provider: provider,
        ModelId: modelId,
        ReservedAtUtc: reservedAtUtc);
}

/// <summary>
/// The insert-only durable PRE-CALL attempt ledger (spec 187 §3), implemented in Infrastructure under the
/// typing output root. It is the SOLE authority for new attempt occupancy: the generator reserves an
/// ordinal here and only the winner is permitted to invoke the provider.
///
/// <para>
/// <b>Occupancy is the union of</b> the ordinals this ledger holds AND the LEGACY outcome records that
/// carry no <c>AttemptReservationId</c> (records written before spec 187). That union is what stops old
/// attempts being forgotten without double-counting a modern, linked outcome — see
/// <c>NewsTypingGenerator</c> for the exact next-ordinal derivation.
/// </para>
/// </summary>
public interface INewsTypingAttemptLedger
{
    /// <summary>
    /// Every persisted reservation, in deterministic (<c>ReservedAtUtc</c>, <c>ReservationId</c>) order
    /// (AD-3). A disk failure degrades to what could be read (logged), never an exception; cancellation
    /// propagates. One read per pass feeds every cohort's occupancy map.
    /// </summary>
    Task<IReadOnlyList<NewsTypingAttemptReservation>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Atomically claims <paramref name="reservation"/>'s ordinal. Returns <c>true</c> ONLY when THIS caller
    /// created the durable file — that is the permission to make one hosted provider call.
    ///
    /// <para>
    /// <b><c>false</c> means: do not call the provider, and SKIP this observation for this pass.</b> Either
    /// another process/invocation already won this ordinal (its call is in flight or already recorded) or
    /// the ledger write failed. It must NOT be retried at the following ordinal: doing so would mint a
    /// second concurrent call for the same input, which is precisely the overspend this ledger exists to
    /// make impossible. The observation is simply re-considered by a later pass, whose occupancy read then
    /// includes whatever the winner recorded.
    /// </para>
    /// Never throws for its own storage failures (Warning + <c>false</c>); cancellation propagates.
    /// </summary>
    Task<bool> TryReserveAsync(NewsTypingAttemptReservation reservation, CancellationToken ct);
}
