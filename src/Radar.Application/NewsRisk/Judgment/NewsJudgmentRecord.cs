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

    /// <summary>
    /// Spec 187 §1 — NO model call was made: this (cohort, company, family set) had already spent
    /// <c>MaxJudgmentAttempts</c> call-producing attempts. Appended LAST so the existing vocabulary is not
    /// renumbered. It is NOT a completed judgment (see <see cref="NewsJudgmentRecord.IsCompletedJudgment"/>),
    /// it does NOT itself count as an attempt, and it renders <c>? unassessed (retries-exhausted)</c> — a
    /// bound that is VISIBLE rather than an unexplained silence.
    /// </summary>
    AttemptsExhausted,
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

/// <summary>
/// The cost/safety limits in force for an attempt (recorded on every judgment, hashed into NO scoring
/// fingerprint). <c>MaxJudgmentAttempts</c> is TRAILING and NULLABLE: a record written before spec 187
/// hydrates as "not recorded", never as a fabricated bound.
/// </summary>
public sealed record NewsJudgmentLimitsRecord(
    int MaxCompaniesPerRun, int MaxFamiliesPerJudgment, int? MaxJudgmentAttempts = null);

/// <summary>
/// One durably persisted direction-judgment ATTEMPT (spec 185 §5) — one company × one judge reader × one
/// stage-1 typing cohort. Carries the full provenance chain: run id, judge provider/model +
/// prompt/schema versions, the FULL stage-1 cohort identity (extractor cohort key + taxonomy version/hash +
/// family-builder identity), the composed stage-2 cohort key, the ordered family-set hash, the supplied
/// family references, ALL FIVE completeness dimensions (archive capture, search enumeration, observation
/// supply, typing completeness, family bundle — spec 182's three verbatim plus the two this pipeline adds),
/// the validated result with drop accounting, the bounded raw-response hash, the limits in force, and
/// creation time. Never a scoring input; never hashed into any fingerprint.
/// <para>
/// <see cref="ProviderDurationMs"/> (spec 187 §7) is TRAILING and NULLABLE, the repo's established
/// convention for an additive persisted field (spec 142's <c>EvidenceQuality</c>, spec 148's
/// <c>EffectiveScoringConfig.Window</c>, spec 186's typing limits). The schema tag is NOT bumped for it:
/// <see cref="CurrentSchemaVersion"/> moved to <c>news-judgment-v2</c> in that same slice for
/// <see cref="TrajectoryFactIds"/> — a field that changes what a record MEANS — whereas a duration changes
/// nothing about how any record is interpreted and its own nullability is the whole "not recorded" story.
/// (Spec 189 §2 later moved the tag to <c>news-judgment-v3</c> for the widened
/// <see cref="TypingCompleteness"/> vocabulary, on the same "changes what a record means" test.)
/// </para>
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
    DateTimeOffset CreatedAtUtc,
    // Spec 187 §1: the supplied FactIds the judge said ESTABLISH BusinessTrajectory. TRAILING and NULLABLE
    // for old-file hydration — a v1 record has no such field, and null means "not recorded under v1",
    // NEVER an empty v2 evidence set and never proof of invalidity. A v2 Judged record always writes a
    // non-null list (empty iff the trajectory is Unknown).
    IReadOnlyList<Guid>? TrajectoryFactIds = null,
    // Spec 187 §7: how long the hosted judgment call took, measured with the injected TimeProvider's
    // MONOTONIC timestamp APIs. TRAILING and NULLABLE, and observational PROVENANCE ONLY — it enters no
    // record id, cohort key, family-set hash, scoring identity or fingerprint, and no selection, ordering
    // or marker decision reads it (AD-3). `null` means NO CALL WAS MADE (a cache reuse, InsufficientFacts,
    // AttemptsExhausted), never "a call took no time"; a provider, parse or validation failure that
    // reached the provider RETAINS its duration, because a slow failure is worth seeing.
    double? ProviderDurationMs = null,
    // Spec 192 §2: the rationale-length facts, so the soft bound still MEANS something now that it flags
    // instead of discarding the response's findings. TRAILING and NULLABLE per the repo's established
    // convention for an additive persisted field (spec 142's EvidenceQuality, spec 148's
    // EffectiveScoringConfig.Window): `null` means NOT RECORDED — a pre-192 record, or an attempt that
    // never produced a validated response (a provider or parse failure) — and NEVER a fabricated `false`
    // or a fabricated 0. RationaleLength is the length of the rationale AS PERSISTED (trimmed and
    // advice-scrubbed), so it can never disagree with the text beside it. Observational provenance only:
    // neither field enters an id, a cohort key, a marker decision, a score or any fingerprint.
    int? RationaleLength = null,
    bool? RationaleOverSoftLimit = null)
{
    /// <summary>
    /// The judgment store schema version stamped on every NEWLY written record. Forked to <c>v2</c> by
    /// spec 187 §1 (the record gained <c>TrajectoryFactIds</c>) and to <c>v3</c> by spec 189 §2, because the
    /// persisted <see cref="TypingCompleteness"/> VOCABULARY changed: a newly written record may now carry
    /// <c>RetryableFailure</c> or <c>RetryExhausted</c> where a v2 record could only say <c>Failed</c>. v1
    /// and v2 records on disk stay readable, are never rewritten, and are NEVER re-classified into a guessed
    /// retryable/exhausted state (AD-8).
    /// <para>
    /// <b>Only the record tag moves.</b> <see cref="NewsJudgmentContract.PromptVersion"/>,
    /// <see cref="NewsJudgmentContract.SchemaVersion"/>, the stage-2 cohort key and the model request are
    /// UNCHANGED — typing completeness is run provenance the judge never sees, so widening it must not fork a
    /// cohort or invalidate a cached verdict. Asserted by test.
    /// </para>
    /// <para>
    /// <b>Spec 192 does NOT bump it</b>, and the distinction is the same one v3 was granted on: it removes
    /// no field, re-means no field and widens no persisted vocabulary. It only APPENDS
    /// <see cref="RationaleLength"/> and <see cref="RationaleOverSoftLimit"/>, both trailing and nullable,
    /// whose own nullability is the entire "not recorded on a pre-192 record" story — the spec-142
    /// <c>EvidenceQuality</c> / spec-148 <c>EffectiveScoringConfig.Window</c> precedent.
    /// </para>
    /// </summary>
    public const string CurrentSchemaVersion = "news-judgment-v3";

    /// <summary>
    /// Whether this attempt is a COMPLETED judgment (reusable through the cache) rather than a named
    /// non-result. The spec-181 rule, not spec 179's: <see cref="NewsJudgmentStatus.ValidationFailed"/> is
    /// NOT completed, so a prompt-confused company is retried by a later run instead of being frozen.
    /// </summary>
    public bool IsCompletedJudgment => Status
        is NewsJudgmentStatus.Judged
        or NewsJudgmentStatus.InsufficientFacts;

    /// <summary>
    /// Whether this record represents a HOSTED CALL that was actually spent (spec 187 §1's attempt bound).
    /// <see cref="NewsJudgmentStatus.Judged"/>, <see cref="NewsJudgmentStatus.ValidationFailed"/>,
    /// <see cref="NewsJudgmentStatus.ProviderFailure"/> and <see cref="NewsJudgmentStatus.ParseFailure"/>
    /// each consumed one call; <see cref="NewsJudgmentStatus.InsufficientFacts"/> (no families, no call),
    /// <see cref="NewsJudgmentStatus.AttemptsExhausted"/> (the bound itself) and a CACHE REUSE
    /// (<see cref="ReusedFromJudgmentId"/> set — a replayed verdict, no provider request) did not.
    /// </summary>
    public bool IsCallProducingAttempt => ReusedFromJudgmentId is null && Status
        is NewsJudgmentStatus.Judged
        or NewsJudgmentStatus.ValidationFailed
        or NewsJudgmentStatus.ProviderFailure
        or NewsJudgmentStatus.ParseFailure;

    /// <summary>
    /// The deterministic per-attempt identity: stage-2 cohort (judge + prompt/schema + stage-1 cohort +
    /// family-builder identity) + company + the ordered family-set hash + run scope. Re-running the SAME
    /// run is idempotent (same id, insert-only store dedupes); the run token is part of the identity so a
    /// NON-completed attempt can be retried by a later run without colliding with its own durable failure
    /// record — the completed-judgment CACHE (which ignores the run) is what prevents duplicate completed
    /// work (the spec-181 mechanism).
    /// <para>
    /// Spec 187 §1 preserves the spec-186 §2 TYPING precedent for the supported null-run path: the
    /// STANDALONE scope additionally folds <paramref name="attemptNumber"/>, because without it every
    /// standalone invocation minted the same id — a real hosted call was made while the insert-only store
    /// silently deduplicated its record and the attempt count never advanced, i.e. an unbounded call
    /// budget. Attempt 1 keeps the ORIGINAL <c>standalone</c> token, so every id already on disk is
    /// byte-unchanged, and the run-scoped branch is untouched for the same reason. The ordinal is derived
    /// ONCE from the PRE-PASS store snapshot — deterministic, clock-free (AD-3).
    /// </para>
    /// </summary>
    public static Guid IdentityFor(
        string cohortKey, Guid companyId, string familySetHash, Guid? runId, int attemptNumber = 1) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:news-judgment:{cohortKey}:{companyId:D}:{familySetHash}:"
                + RunScope(runId, attemptNumber));

    /// <summary>
    /// The deterministic identity of a NO-CALL <see cref="NewsJudgmentStatus.AttemptsExhausted"/> record
    /// (spec 187 §1). It lives in its OWN namespace segment (<c>news-judgment-exhausted</c>) and folds the
    /// CURRENT run scope — the non-null run id, or the literal <c>standalone</c> for the null-run path:
    /// <list type="bullet">
    /// <item>the separate namespace makes collision with the last <c>standalone#N</c> CALL attempt
    /// structurally impossible, so an exhaustion marker can never be mistaken for a spent call;</item>
    /// <item>folding the run scope means a LATER real run persists ONE small fresh exhaustion record and
    /// therefore satisfies spec 185's same-run marker rule — without it the row would dedupe onto a prior
    /// run's record and render <c>stale</c>, hiding the bound behind an unrelated reason; and</item>
    /// <item>repeated exhausted NULL-run invocations idempotently reuse the single <c>standalone</c>
    /// exhaustion record (both the record and the current run scope are null), so the marker stays
    /// <c>retries-exhausted</c> and no call occurs. No clock and no counter enter this id (AD-3).</item>
    /// </list>
    /// </summary>
    public static Guid ExhaustionIdentityFor(
        string cohortKey, Guid companyId, string familySetHash, Guid? runId) =>
        DeterministicGuid.FromCanonicalString(
            $"radar:news-judgment-exhausted:{cohortKey}:{companyId:D}:{familySetHash}:"
                + (runId is { } id ? id.ToString("D") : "standalone"));

    private static string RunScope(Guid? runId, int attemptNumber) => runId is { } id
        ? id.ToString("D")
        : attemptNumber <= 1
            ? "standalone"
            : FormattableString.Invariant($"standalone#{attemptNumber}");
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
