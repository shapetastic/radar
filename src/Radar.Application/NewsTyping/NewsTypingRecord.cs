using Radar.Application.Identity;
using Radar.Application.News;

namespace Radar.Application.NewsTyping;

/// <summary>
/// The CLOSED per-attempt status vocabulary (spec 181 §4). EVERY attempt is persisted — provider error,
/// parse error and validation failure included — so absence of a record is never mistakable for a clean
/// result. Only <see cref="Typed"/> and <see cref="InsufficientContent"/> are COMPLETED typings (reusable
/// through the cache); a failure is recorded but retried by a later run.
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

/// <summary>The cost/safety limits in force for an attempt (recorded on every typing record, hashed into NO scoring fingerprint).</summary>
public sealed record NewsTypingLimitsRecord(int MaxNewTypingsPerRun, int LookbackDays);

/// <summary>
/// One durably persisted news-typing ATTEMPT (spec 181 §4) — one observation × one reader cohort. Carries
/// the full provenance chain: observation id + payload hash + capture mode, reader/provider/model,
/// prompt/schema/taxonomy versions + taxonomy hash + cohort key, the validated relevance/facts with drop
/// accounting, the bounded raw-response hash, the limits in force, and creation time. Never a scoring input;
/// never hashed into any fingerprint.
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
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>The typing store schema version stamped on every record.</summary>
    public const string CurrentSchemaVersion = "news-typing-v1";

    /// <summary>
    /// Whether this attempt is a COMPLETED typing (reusable through the cache) rather than a named
    /// non-result. Deliberately NARROWER than spec 179's rule: <see cref="NewsTypingStatus.ValidationFailed"/>
    /// is NOT completed here, so a prompt-confused observation is retried by a later run instead of being
    /// frozen as permanently untypeable.
    /// </summary>
    public bool IsCompletedTyping => Status
        is NewsTypingStatus.Typed
        or NewsTypingStatus.InsufficientContent;

    /// <summary>
    /// The deterministic per-attempt identity: cohort (provider + model + prompt/schema/taxonomy) +
    /// observation + payload hash + run scope. Re-running the SAME run is idempotent (same id, insert-only
    /// store dedupes); the run token is deliberately part of the identity so a NON-completed attempt
    /// (provider failure, validation failure) can be retried by a later run without colliding with its own
    /// durable failure record in the insert-only store — the completed-typing CACHE (which ignores the run)
    /// is what prevents duplicate completed work.
    /// </summary>
    public static Guid IdentityFor(string cohortKey, Guid observationId, string payloadHash, Guid? runId) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:news-typing:{cohortKey}:{observationId:D}:{payloadHash}:"
                + (runId is { } id ? id.ToString("D") : "standalone"));
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
