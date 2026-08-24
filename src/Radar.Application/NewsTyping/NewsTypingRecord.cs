using Radar.Application.Identity;
using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The CLOSED per-attempt status vocabulary (spec 181 §4). EVERY attempt is persisted — provider error,
/// parse error and validation failure included — so absence of a record is never mistakable for a clean
/// result. Only <see cref="Typed"/> and <see cref="InsufficientContent"/> are COMPLETED typings (reusable
/// through the cache); a failure is recorded and retried by a later run — BOUNDED by
/// <see cref="NewsTypingOptions.MaxTypingAttempts"/> hosted calls, after which the observation leaves
/// selection instead of re-entering the budget forever.
/// <para>
/// Spec 187 §3 moved WHERE that bound is enforced: the durable PRE-CALL
/// <see cref="INewsTypingAttemptLedger"/> reservation is now the authority, because an outcome record is
/// written AFTER the call and therefore cannot bound it (a crash or a failed outcome write consumed a call
/// and advanced nothing). These status records remain the durable evidence of what each attempt PRODUCED;
/// counting them survives only as the explicit legacy-occupancy migration read for pre-187 records.
/// </para>
/// </summary>
public enum NewsTypingStatus
{
    /// <summary>Completed: relevance parsed and every emitted fact either survived validation or was named-dropped (with at least one survivor when any was emitted).</summary>
    Typed = 0,

    /// <summary>Completed: the model judged the supplied text too thin to type. NOT a defect — expected for headline-only legacy capture.</summary>
    InsufficientContent,

    /// <summary>The model responded but the relevance token was invalid, or it emitted facts of which none survived validation.</summary>
    ValidationFailed,

    /// <summary>The provider was unreachable/errored at run time. Never blocks another reader; retried by a later run.</summary>
    ProviderFailure,

    /// <summary>The provider answered but no typed response could be parsed.</summary>
    ParseFailure,

    /// <summary>The observation supplied no citable text at all; no model call was made.</summary>
    NoContent,
}

/// <summary>
/// The cost/safety limits in force for an attempt (recorded on every typing record, hashed into NO scoring
/// fingerprint). <see cref="MaxTypingAttempts"/> and <see cref="MaxRetryTypingsPerRun"/> are TRAILING and
/// NULLABLE (spec 186 §2), as is <see cref="MaxCandidateTypingsPerRun"/> (spec 187 §2): a record written
/// before the slice that added a limit carries <c>null</c> for it, and that <c>null</c> reads as "not
/// recorded" — never as a fabricated limit. The record schema tag is deliberately unchanged: the additive
/// fields are trailing and nullable, so every accrued file still hydrates losslessly and its own
/// nullability IS the "not recorded" marker (the CLAUDE.md convention for additive trailing-nullable
/// persisted fields).
/// </summary>
public sealed record NewsTypingLimitsRecord(
    int MaxNewTypingsPerRun,
    int LookbackDays,
    int? MaxTypingAttempts,
    int? MaxRetryTypingsPerRun,
    int? MaxCandidateTypingsPerRun = null);

/// <summary>
/// One durably persisted news-typing ATTEMPT (spec 181 §4) — one observation × one reader cohort. Carries
/// the full provenance chain: observation id + payload hash + capture mode, reader/provider/model,
/// prompt/schema/taxonomy versions + taxonomy hash + cohort key, the validated relevance/facts with drop
/// accounting, the bounded raw-response hash, the limits in force, and creation time. Never a scoring input;
/// never hashed into any fingerprint.
/// <para>
/// <see cref="AttemptReservationId"/> and <see cref="AttemptOrdinal"/> are TRAILING and NULLABLE (spec 187
/// §3): they LINK this outcome back to the durable pre-call reservation that permitted the hosted call. A
/// record written before spec 187 carries neither, and that <c>null</c> pair IS the "legacy attempt" marker
/// the occupancy migration reads — never a fabricated link. The record schema tag is deliberately unchanged
/// (<see cref="CurrentSchemaVersion"/>): the repo's established convention for additive trailing-nullable
/// persisted fields (spec 142's <c>EvidenceQuality</c>, spec 148's <c>EffectiveScoringConfig.Window</c>,
/// spec 186's typing limits), so every accrued file still hydrates losslessly and its own nullability
/// carries the "not recorded" meaning.
/// </para>
/// <para>
/// <see cref="ProviderDurationMs"/> (spec 187 §7) follows the SAME convention for the same reason, and the
/// schema tag is again deliberately unchanged: it is trailing, nullable, additive, read by no existing
/// consumer, and its own nullability already carries the only meaning a missing value could have ("no
/// hosted call was made for this record"). Bumping the tag would force every accrued file to be re-read as
/// a different schema for a field that changes nothing about how the record is interpreted.
/// </para>
/// </summary>
public sealed record NewsTypingRecord(
    string SchemaVersion,
    Guid TypingId,
    Guid? RunId,
    Guid ObservationId,
    string PayloadHash,
    Guid? CompanyId,
    string? Ticker,
    NewsObservationCaptureMode CaptureMode,
    string ReaderName,
    string Provider,
    string ModelId,
    string PromptVersion,
    string ResultSchemaVersion,
    string TaxonomyVersion,
    string TaxonomyHash,
    string CohortKey,
    NewsTypingRelevance? Relevance,
    NewsEventType? DerivedPrimaryType,
    IReadOnlyList<NewsTypingValidatedFact> Facts,
    int FactsTotal,
    int FactsAccepted,
    int FactsDropped,
    IReadOnlyList<string> FactDropReasons,
    NewsTypingStatus Status,
    string? RawResponseHash,
    string? FailureDetail,
    NewsTypingLimitsRecord Limits,
    Guid? ReusedFromTypingId,
    DateTimeOffset CreatedAtUtc,
    Guid? AttemptReservationId = null,
    int? AttemptOrdinal = null,
    // Spec 187 §7: how long the hosted extraction call took, measured with the injected TimeProvider's
    // MONOTONIC timestamp APIs. TRAILING and NULLABLE, and observational PROVENANCE ONLY — it enters no
    // record id, cohort key, family id, scoring identity or fingerprint, and no selection or ordering
    // decision reads it (AD-3). `null` means NO CALL WAS MADE for this record (a cache reuse, a NoContent
    // observation), never "a call took no time"; a provider, parse or validation failure that reached the
    // provider RETAINS its duration, because a slow failure is exactly the thing worth seeing.
    double? ProviderDurationMs = null)
{
    /// <summary>The typing store schema version stamped on every record.</summary>
    public const string CurrentSchemaVersion = "news-typing-v1";

    /// <summary>
    /// Whether this attempt is a COMPLETED typing (reusable through the cache) rather than a named
    /// non-result. Deliberately NARROWER than spec 179's rule: <see cref="NewsTypingStatus.ValidationFailed"/>
    /// is NOT completed here, so a prompt-confused observation is retried by a later run instead of being
    /// frozen as permanently untypeable — within the attempt bound (spec 186 §2, enforced pre-call since
    /// spec 187 §3), which stops "retried by a later run" from meaning "retried by EVERY later run".
    /// </summary>
    public bool IsCompletedTyping => Status
        is NewsTypingStatus.Typed
        or NewsTypingStatus.InsufficientContent;

    /// <summary>
    /// The deterministic per-attempt identity: cohort (provider + model + prompt/schema/taxonomy) +
    /// observation + payload hash + attempt scope. Re-running the SAME run is idempotent (same id,
    /// insert-only store dedupes); the run token is deliberately part of the identity so a NON-completed
    /// attempt (provider failure, validation failure) can be retried by a later run without colliding with
    /// its own durable failure record in the insert-only store — the completed-typing CACHE (which ignores
    /// the run) is what prevents duplicate completed work.
    /// <para>
    /// Spec 186 §2: the STANDALONE (null-run) scope additionally folds
    /// <paramref name="attemptNumber"/>. Without it every standalone invocation minted the same "standalone"
    /// id, so a real hosted call was made while the insert-only store silently deduplicated its record and
    /// the attempt count never advanced — an unbounded call budget. The token is deterministic (no clock, no
    /// randomness — AD-3), and attempt 1 keeps the ORIGINAL "standalone" token, so every id already on disk
    /// is byte-unchanged. The run-scoped branch is untouched for the same reason.
    /// </para>
    /// <para>
    /// Spec 187 §3 changed only WHERE <paramref name="attemptNumber"/> comes from, never the identity
    /// shape: it is now the ordinal of the durable pre-call
    /// <see cref="NewsTypingAttemptReservation"/> this outcome is linked to, rather than a count derived
    /// from outcome records after the fact. The two agree exactly for a purely pre-187 history (legacy
    /// outcomes occupy the low ordinals), so every accrued <c>standalone</c>/<c>standalone#N</c> id is
    /// byte-unchanged.
    /// </para>
    /// </summary>
    public static Guid IdentityFor(
        string cohortKey,
        Guid observationId,
        string payloadHash,
        Guid? runId,
        int attemptNumber = 1) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:news-typing:{cohortKey}:{observationId:D}:{payloadHash}:"
                + (runId is { } id
                    ? id.ToString("D")
                    : attemptNumber <= 1
                        ? "standalone"
                        : FormattableString.Invariant($"standalone#{attemptNumber}")));
}

/// <summary>
/// The insert-only durable typing store (spec 181 §4), implemented in Infrastructure. Write-once per
/// deterministic id; the cache read returns only COMPLETED typings for (cohort, observation, payload) —
/// a provider/parse/validation failure is persisted but never reused, so a retry may genuinely succeed.
/// </summary>
public interface INewsTypingStore
{
    /// <summary>Persists the attempt if its id is new. Never throws for a disk failure (Warning + false); cancellation propagates.</summary>
    Task<bool> WriteAsync(NewsTypingRecord record, CancellationToken ct);

    /// <summary>Every persisted attempt, in deterministic (<c>CreatedAtUtc</c>, <c>TypingId</c>) order (AD-3).</summary>
    Task<IReadOnlyList<NewsTypingRecord>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// The most recent COMPLETED typing for (cohort, observation, payload), or <c>null</c>. This is the
    /// cache: the same model/prompt/schema/taxonomy over the same immutable observation is never typed
    /// twice; any policy, model or taxonomy change composes a different cohort key and therefore misses.
    /// </summary>
    Task<NewsTypingRecord?> FindCompletedAsync(
        string cohortKey, Guid observationId, string payloadHash, CancellationToken ct);
}
