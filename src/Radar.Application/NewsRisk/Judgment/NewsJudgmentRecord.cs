using Radar.Application.Identity;
using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The CLOSED per-attempt status vocabulary (spec 185 §5). EVERY attempt is persisted — no facts, provider
/// error, parse error and validation failure included — so absence of a record is never mistakable for a
/// clean result. Only <see cref="Judged"/> and <see cref="InsufficientFacts"/> are COMPLETED judgments
/// (reusable through the cache); a failure is recorded but retried by a later run. A
/// <see cref="ValidationFailed"/> response — including one whose findings were ALL invalid — renders
/// <c>? unassessed</c> on the leaders, NEVER "no challenge found in supplied facts".
/// </summary>
public enum NewsJudgmentStatus
{
    /// <summary>Completed: the trajectory parsed and every emitted finding either survived validation or was named-dropped (with at least one survivor when any was emitted).</summary>
    Judged = 0,

    /// <summary>Completed: zero canonical families were available for the company; no model call was made.</summary>
    InsufficientFacts,

    /// <summary>The model responded but the trajectory was invalid, the strength was out of range, or every emitted finding failed validation.</summary>
    ValidationFailed,

    /// <summary>The provider was unreachable/errored at run time. Never blocks another judge; retried by a later run.</summary>
    ProviderFailure,

    /// <summary>The provider answered but no typed response could be parsed.</summary>
    ParseFailure,
}

/// <summary>
/// Whether a bound truncated the families supplied to the judge (spec 185 §5). The zero value is
/// DELIBERATELY the degraded state (the spec-182 convention): a record missing the field must read as
/// capped, never as complete.
/// </summary>
public enum NewsJudgmentFamilyBundle
{
    /// <summary>Families were dropped by <c>MaxFamiliesPerJudgment</c> — "no challenge" is over a subset.</summary>
    Capped = 0,

    /// <summary>Every resolvable family for the company was supplied.</summary>
    Complete,
}

/// <summary>One supplied family's provenance reference — enough to resolve judgment → family → representative fact → excerpt → observation → archive through the typing store.</summary>
public sealed record NewsJudgmentFamilyRef(
    Guid FamilyId,
    Guid RepresentativeFactId,
    int MemberCount,
    int DistinctPublisherCount);

/// <summary>The cost/safety limits in force for an attempt (recorded on every judgment, hashed into NO scoring fingerprint).</summary>
public sealed record NewsJudgmentLimitsRecord(int MaxCompaniesPerRun, int MaxFamiliesPerJudgment);

/// <summary>
/// One durably persisted direction-judgment ATTEMPT (spec 185 §5) — one company × one judge reader × one
/// stage-1 typing cohort. Carries the full provenance chain: run id, judge provider/model +
/// prompt/schema versions, the FULL stage-1 cohort identity (extractor cohort key + taxonomy version/hash +
/// family-builder identity), the composed stage-2 cohort key, the ordered family-set hash, the supplied
/// family references, ALL FIVE completeness dimensions (archive capture, search enumeration, observation
/// supply, typing completeness, family bundle — spec 182's three verbatim plus the two this pipeline adds),
/// the validated result with drop accounting, the bounded raw-response hash, the limits in force, and
/// creation time. Never a scoring input; never hashed into any fingerprint.
/// </summary>
public sealed record NewsJudgmentRecord(
    string SchemaVersion,
    Guid JudgmentId,
    Guid? RunId,
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    string JudgeName,
    string Provider,
    string ModelId,
    string PromptVersion,
    string ResultSchemaVersion,
    string Stage1CohortKey,
    string TaxonomyVersion,
    string TaxonomyHash,
    string FamilyBuilderIdentity,
    string CohortKey,
    string FamilySetHash,
    IReadOnlyList<NewsJudgmentFamilyRef> Families,
    NewsRiskArchiveCapture ArchiveCapture,
    NewsRiskSearchEnumeration SearchEnumeration,
    NewsRiskAssessmentBundle ObservationSupply,
    NewsTypingCompleteness TypingCompleteness,
    NewsJudgmentFamilyBundle FamilyBundle,
    IReadOnlyList<string> CoverageIssues,
    NewsJudgmentStatus Status,
    NewsJudgmentTrajectory? BusinessTrajectory,
    int? ChallengeStrength,
    IReadOnlyList<NewsJudgmentValidatedFinding> Findings,
    string? Rationale,
    int FindingsTotal,
    int FindingsAccepted,
    int FindingsDropped,
    IReadOnlyList<string> FindingDropReasons,
    string? RawResponseHash,
    string? FailureDetail,
    NewsJudgmentLimitsRecord Limits,
    Guid? ReusedFromJudgmentId,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>The judgment store schema version stamped on every record.</summary>
    public const string CurrentSchemaVersion = "news-judgment-v1";

    /// <summary>
    /// Whether this attempt is a COMPLETED judgment (reusable through the cache) rather than a named
    /// non-result. The spec-181 rule, not spec 179's: <see cref="NewsJudgmentStatus.ValidationFailed"/> is
    /// NOT completed, so a prompt-confused company is retried by a later run instead of being frozen.
    /// </summary>
    public bool IsCompletedJudgment => Status
        is NewsJudgmentStatus.Judged
        or NewsJudgmentStatus.InsufficientFacts;

    /// <summary>
    /// The deterministic per-attempt identity: stage-2 cohort (judge + prompt/schema + stage-1 cohort +
    /// family-builder identity) + company + the ordered family-set hash + run scope. Re-running the SAME
    /// run is idempotent (same id, insert-only store dedupes); the run token is part of the identity so a
    /// NON-completed attempt can be retried by a later run without colliding with its own durable failure
    /// record — the completed-judgment CACHE (which ignores the run) is what prevents duplicate completed
    /// work (the spec-181 mechanism).
    /// </summary>
    public static Guid IdentityFor(string cohortKey, Guid companyId, string familySetHash, Guid? runId) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:news-judgment:{cohortKey}:{companyId:D}:{familySetHash}:"
                + (runId is { } id ? id.ToString("D") : "standalone"));
}

/// <summary>
/// The ONE definition of the judgment store's folder segment beneath the news-risk output root (spec 185
/// §5's layout). Shared so the Infrastructure store that WRITES the path and the report that CITES it for
/// traceability (spec 186 §1) cannot drift apart.
/// </summary>
public static class NewsJudgmentStoreLayout
{
    /// <summary>The folder segment: <c>{newsRiskRoot}/judgments/…</c>.</summary>
    public const string JudgmentsFolder = "judgments";

    /// <summary>The store root for a news-risk output root — the path the weekly report states ONCE.</summary>
    public static string RootFor(string outputDirectory) =>
        Path.Combine(outputDirectory, JudgmentsFolder);
}

/// <summary>
/// The insert-only durable judgment store (spec 185 §5), implemented in Infrastructure at
/// <c>{newsRiskRoot}/judgments/{judge-policy-segment}/{companyId}/{judgmentId}.json</c>. Write-once per
/// deterministic id; the cache read returns only COMPLETED judgments for (cohort, company, family set) —
/// a provider/parse/validation failure is persisted but never reused, so a retry may genuinely succeed.
/// </summary>
public interface INewsJudgmentStore
{
    /// <summary>Persists the attempt if its id is new. Never throws for a disk failure (Warning + false); cancellation propagates.</summary>
    Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct);

    /// <summary>Every persisted attempt, in deterministic (<c>CreatedAtUtc</c>, <c>JudgmentId</c>) order (AD-3).</summary>
    Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// The most recent COMPLETED judgment for (cohort, company, ordered family set), or <c>null</c>. This
    /// is the cache: the same judge/prompt/schema over the same stage-1 cohort's same family set is never
    /// judged twice; any policy, model, taxonomy or family change composes a different key and misses.
    /// </summary>
    Task<NewsJudgmentRecord?> FindCompletedAsync(
        string cohortKey, Guid companyId, string familySetHash, CancellationToken ct);
}
