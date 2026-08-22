using Radar.Application.Identity;
using Radar.Application.News;

namespace Radar.Application.NewsRisk;

/// <summary>
/// The CLOSED per-attempt status vocabulary. EVERY attempt is persisted (spec 179 §6) — no content,
/// provider error, parse error and validation failure included — so absence of a record is never
/// mistakable for a clean result. The first three are completed analyses; the rest are named
/// non-results, none of which may ever render as "no risk".
/// </summary>
public enum NewsRiskAssessmentStatus
{
    /// <summary>Completed: the supplied text supports ≥ 1 validated risk claim.</summary>
    ThesisChallenged = 0,

    /// <summary>
    /// Completed: sufficient supplied text, no supported risk — a statement about the SUPPLIED TEXT ONLY,
    /// rendered with this run's three completeness dimensions stated beside it (spec 182 §3). Never an
    /// all-clear about the company.
    /// </summary>
    NoRiskFoundInSuppliedText,

    /// <summary>Completed: the supplied text was too thin to assess. Not a low score.</summary>
    InsufficientContent,

    /// <summary>The model responded but no claim survived §6 validation (or the response was out of range).</summary>
    ValidationFailed,

    /// <summary>The provider was unreachable/errored at run time. Never blocks other readers.</summary>
    ProviderFailure,

    /// <summary>The provider answered but no typed response could be parsed.</summary>
    ParseFailure,

    /// <summary>The point-in-time bundle held zero admissible articles; no model call was made.</summary>
    NoContent,

    /// <summary>
    /// RETIRED by spec 182 (never produced again): under spec 179 §4, incomplete newssearch coverage
    /// blocked the model call entirely. Spec 182 records completeness as three independent dimensions
    /// (<see cref="NewsRiskArchiveCapture"/>/<see cref="NewsRiskSearchEnumeration"/>/
    /// <see cref="NewsRiskAssessmentBundle"/>) instead — completeness gates only ABSENCE claims, never
    /// presence claims. The member stays so accrued v1 records still deserialize.
    /// </summary>
    [Obsolete("Spec 182 retired this status in favour of the recorded completeness dimensions; it is never produced.")]
    IncompleteCoverage,
}

/// <summary>Which text fields of one observation were ACTUALLY supplied to the model — the §6 "archived text" definition, frozen per attempt.</summary>
public sealed record NewsRiskInputObservationRef(
    Guid ObservationId,
    string PayloadHash,
    bool DescriptionSupplied,
    bool BodySupplied,
    string? BodyContentHash,
    DateTimeOffset? BodyRetrievedAtUtc,
    string? BodyExtractorVersion,
    string? BodyRetrievalPolicy,
    NewsObservationCaptureMode CaptureMode);

/// <summary>The cost/safety limits in force for an attempt (spec 179 §11: recorded in assessments, hashed into no scoring fingerprint).</summary>
public sealed record NewsRiskShadowLimitsRecord(
    int LookbackDays,
    int MaxCompaniesPerRun,
    int MaxArticlesPerCompany,
    int MaxFetchedArticlesPerCompany);

/// <summary>
/// One durably persisted news-risk assessment ATTEMPT (spec 179 §6) — the frozen predictor the §9 evaluator
/// later joins to forward prices. Carries the full provenance list: durable run id, selection/assessment
/// cutoffs, selecting strategy/rank/snapshot facts, ordered observation ids + payload/body hashes,
/// the three spec-182 completeness dimensions, provider/exact model id, prompt/schema versions, the bounded
/// raw-response hash, the §6-validated result, and creation time. Never a scoring input; never hashed into
/// any fingerprint.
/// <para>
/// v2 (spec 182) replaced the v1 <c>CoverageComplete</c> boolean with the three INDEPENDENT dimension
/// fields. A v1 JSON file deserializes safely: its extra <c>coverageComplete</c> property is ignored, and
/// its missing dimension fields default to each enum's zero value — deliberately the DEGRADED state on
/// every dimension, so no legacy record can ever read as best-state.
/// </para>
/// </summary>
public sealed record NewsRiskAssessmentRecord(
    string SchemaVersion,
    Guid AssessmentId,
    Guid RunId,
    DateTimeOffset SelectionAsOfUtc,
    DateTimeOffset AssessmentCutoffUtc,
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    IReadOnlyList<NewsRiskCandidateSelection> Selections,
    string ReaderName,
    string Provider,
    string ModelId,
    string PromptVersion,
    string ResultSchemaVersion,
    string CohortKey,
    string InputBundleHash,
    IReadOnlyList<NewsRiskInputObservationRef> Observations,
    NewsRiskArchiveCapture ArchiveCapture,
    NewsRiskSearchEnumeration SearchEnumeration,
    NewsRiskAssessmentBundle AssessmentBundle,
    IReadOnlyList<string> CoverageIssues,
    NewsRiskAssessmentStatus Status,
    int? RiskScore,
    IReadOnlyList<NewsRiskCategory> Categories,
    IReadOnlyList<NewsRiskValidatedClaim> Claims,
    string? Rationale,
    int ClaimsTotal,
    int ClaimsAccepted,
    int ClaimsDropped,
    IReadOnlyList<string> ClaimDropReasons,
    string? RawResponseHash,
    string? FailureDetail,
    NewsRiskShadowLimitsRecord Limits,
    Guid? ReusedFromAssessmentId,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>The archive schema version stamped on every assessment record (v2: spec 182's three completeness dimensions).</summary>
    public const string CurrentSchemaVersion = "news-risk-assessment-v2";

    /// <summary>Whether this attempt is a COMPLETED analysis (reusable through the §6 cache) rather than a named non-result.</summary>
    public bool IsCompletedAnalysis => Status
        is NewsRiskAssessmentStatus.ThesisChallenged
        or NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText
        or NewsRiskAssessmentStatus.InsufficientContent
        or NewsRiskAssessmentStatus.ValidationFailed;

    /// <summary>
    /// The deterministic per-attempt identity: cohort (provider + model + prompt/schema) + ordered
    /// input-bundle hash + run + reader name. Re-running the SAME run is idempotent (same id, insert-only
    /// store dedupes); a policy/model/input change mints a NEW id, so an incompatible assessment is never
    /// overwritten (spec 179 §6).
    /// </summary>
    public static Guid IdentityFor(string cohortKey, string inputBundleHash, Guid runId, string readerName) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:news-risk-assessment:{cohortKey}:{inputBundleHash}:{runId:D}:{readerName}");
}

/// <summary>
/// The insert-only durable assessment store (spec 179 §6), implemented in Infrastructure. Write-once per
/// deterministic id; the cache read returns only COMPLETED analyses (a provider/parse failure is persisted
/// but never reused — a retry may genuinely succeed).
/// </summary>
public interface INewsRiskAssessmentStore
{
    /// <summary>Persists the attempt if its id is new. Never throws for a disk failure (Warning + false); cancellation propagates.</summary>
    Task<bool> WriteAsync(NewsRiskAssessmentRecord record, CancellationToken ct);

    /// <summary>Every persisted attempt, in deterministic (<c>CreatedAtUtc</c>, <c>AssessmentId</c>) order (AD-3).</summary>
    Task<IReadOnlyList<NewsRiskAssessmentRecord>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// The most recent COMPLETED analysis for (cohort, ordered input bundle), or <c>null</c>. This is the §6
    /// cache: same model/prompt/schema over the same ordered inputs is never analyzed twice; any policy or
    /// model change composes a different cohort key and therefore misses.
    /// </summary>
    Task<NewsRiskAssessmentRecord?> FindCompletedAsync(
        string cohortKey, string inputBundleHash, CancellationToken ct);
}
