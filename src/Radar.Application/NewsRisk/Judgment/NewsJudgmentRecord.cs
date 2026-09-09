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

/// <summary>
/// What a persisted directional judgment's cited trajectory facts could establish (spec 214 §2, widened by
/// spec 215 §2). Values are explicit and start at 1 so a defaulted zero is UNDEFINED (refused by the strict
/// file-store enum converter) rather than silently meaningful. Precedence is fixed by
/// <see cref="NewsJudgmentValidator.TrajectoryBasisFor"/>: <see cref="Supported"/> first, then
/// <see cref="ReferenceSupported"/>, else <see cref="LevelOnly"/>.
/// </summary>
public enum NewsTrajectoryBasis
{
    /// <summary>At least one cited trajectory fact is a <see cref="NewsFactComparisonBasis.StatedComparison"/> or an <see cref="NewsFactComparisonBasis.Event"/> — the direction rests on something that can carry one.</summary>
    Supported = 1,

    /// <summary>EVERY cited trajectory fact is <see cref="NewsFactComparisonBasis.LevelOnly"/> or <see cref="NewsFactComparisonBasis.NotQuantified"/> and no cited reference value names a cited level's metric — the judge read a level as a trend. Persisted verbatim, never rewritten; mints no signal.</summary>
    LevelOnly = 2,

    /// <summary>
    /// Spec 215 §2: no cited fact is a stated comparison or an event, but at least one cited trajectory fact
    /// is <see cref="NewsFactComparisonBasis.LevelOnly"/> AND the judge cited a company-reported reference
    /// value (<c>TrajectoryReferenceIds</c>) whose metric that fact's statement NAMES — "backlog $2.5B vs
    /// $2.93B reported in January: down". A reference-supported comparison IS a directional basis, so it
    /// materializes under <c>news-judgment-signal-v3</c> exactly as <see cref="Supported"/> does.
    /// </summary>
    ReferenceSupported = 3,
}

/// <summary>
/// SPEC 219 §2 — how deeply the judge read one company this run. Values are explicit and start at 1 so a
/// defaulted zero is UNDEFINED (refused by the strict file-store enum converter) rather than silently
/// meaningful — the <see cref="NewsTrajectoryBasis"/> precedent.
/// <para>
/// This is not a quality grade and not a preference. It says which BUDGET assembled the input, so a reader
/// never has to infer whether "no challenge found" came from the whole fact set or from five families of it.
/// </para>
/// </summary>
public enum NewsJudgmentReadDepth
{
    /// <summary>The spec-179 §3 depth cohort: the full <c>MaxFamiliesPerJudgment</c> budget.</summary>
    Full = 1,

    /// <summary>The spec-219 §1 breadth cohort: the bounded <c>MaxFamiliesPerBreadthJudgment</c> budget.</summary>
    Breadth = 2,
}

/// <summary>
/// One supplied family's provenance reference — enough to resolve judgment → family → representative fact →
/// excerpt → observation → archive through the typing store.
/// <para>
/// <see cref="ComparisonBasis"/> (spec 214 §1) is TRAILING and NULLABLE: the deterministic
/// <see cref="StatementComparisonClassifier"/> read of the family's representative statement as it was
/// SUPPLIED to the judge. <c>null</c> means the record was written before spec 214 — never defaulted,
/// never re-derived on read (the statement could be re-classified, but the judge never saw the line).
/// </para>
/// </summary>
/// <param name="ObservedAtUtc">
/// SPEC 216 §1 (trailing + nullable): the family's earliest observation instant AS SUPPLIED to the judge —
/// the input the reference-eligibility guard reads, persisted so "which reference could this fact have
/// seen" is answerable from the record alone. <c>null</c> = the record was written before spec 216, or the
/// family carried no recorded instant. Never defaulted, never re-derived on read.
/// </param>
public sealed record NewsJudgmentFamilyRef(
    Guid FamilyId,
    Guid RepresentativeFactId,
    int MemberCount,
    int DistinctPublisherCount,
    NewsFactComparisonBasis? ComparisonBasis = null,
    DateTimeOffset? ObservedAtUtc = null);

/// <summary>
/// SPEC 216 §5 — one projected or cited reference value's identity and KIND, persisted so a reader can tell
/// a comparison against the company's own EARLIER filing (<see cref="NewsJudgmentReferenceKind.Prior"/>)
/// from one against the prior pair the newest release itself stated
/// (<see cref="NewsJudgmentReferenceKind.StatedPrior"/>) without re-projecting a ledger that has since
/// grown.
/// </summary>
public sealed record NewsJudgmentReferenceRef(Guid ReferenceId, NewsJudgmentReferenceKind Kind);

/// <summary>
/// The cost/safety limits in force for an attempt (recorded on every judgment, hashed into NO scoring
/// fingerprint). <c>MaxJudgmentAttempts</c> is TRAILING and NULLABLE: a record written before spec 187
/// hydrates as "not recorded", never as a fabricated bound.
/// <para>
/// <b>WHICH FIELD HOLDS THE BOUND THAT ACTUALLY APPLIED: <see cref="AppliedMaxFamilies"/>, not
/// <see cref="MaxFamiliesPerJudgment"/>.</b> Spec 219 §2 gave the breadth cohort its OWN, much smaller
/// family bound, so <see cref="MaxFamiliesPerJudgment"/> alone would state <c>50</c> on an attempt whose
/// input was in fact cut at <c>5</c> — a false claim on a durable record. That field is deliberately NOT
/// re-meant (every accrued record keeps its meaning: the CONFIGURED depth-cohort bound), and the applied
/// bound is appended instead as a TRAILING NULLABLE: <c>null</c> = NOT RECORDED on a pre-219 record, never
/// a fabricated value. On a spec-219 record it is <c>MaxFamiliesPerBreadthJudgment</c> for a
/// <see cref="NewsJudgmentReadDepth.Breadth"/> attempt and <see cref="MaxFamiliesPerJudgment"/> for a
/// <see cref="NewsJudgmentReadDepth.Full"/> one.
/// </para>
/// </summary>
/// <param name="MaxCompaniesPerRun">The per-run judged-candidate safety valve (spec 219 §1) in force.</param>
/// <param name="MaxFamiliesPerJudgment">The CONFIGURED depth-cohort family bound — not necessarily the one this attempt ran under.</param>
/// <param name="MaxJudgmentAttempts">The spec-187 §1 call bound; <c>null</c> = a pre-187 record.</param>
/// <param name="AppliedMaxFamilies">
/// SPEC 219 §2 — the family bound that ACTUALLY cut this attempt's input. <c>null</c> = not recorded (a
/// pre-219 record), never a fabricated value.
/// </param>
public sealed record NewsJudgmentLimitsRecord(
    int MaxCompaniesPerRun,
    int MaxFamiliesPerJudgment,
    int? MaxJudgmentAttempts = null,
    int? AppliedMaxFamilies = null);

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
    bool? RationaleOverSoftLimit = null,
    // Spec 197 §2.2: how many RAW CITATION OCCURRENCES the shared citation resolver deterministically
    // expanded from a hexadecimal prefix to the complete supplied FactId, across trajectory plus findings.
    // TRAILING and NULLABLE, and the three states are DIFFERENT FACTS:
    //   null     = no validated model response was examined under this contract (a provider or parse
    //              failure, InsufficientFacts, AttemptsExhausted), or a PRE-197 record — never a
    //              fabricated 0;
    //   0        = a response WAS examined and every accepted citation was already complete;
    //   positive = that many raw citation occurrences were expanded, INCLUDING expansions observed before
    //              a different validation error failed the response.
    // Observational provenance only: it enters no id, cohort key, marker decision, score or fingerprint.
    int? FactIdPrefixExpansionCount = null,
    // Spec 214 §2: what the cited TrajectoryFactIds could establish, computed by the validator ONLY for a
    // current, Judged, DIRECTIONAL (Improving/Deteriorating) record over the RESOLVED cited facts. TRAILING
    // and NULLABLE, and `null` means NOT APPLICABLE — a Mixed or Unknown trajectory, a validation failure,
    // an InsufficientFacts/ProviderFailure/ParseFailure/AttemptsExhausted attempt — OR a pre-214 record.
    // The two nulls are distinguishable by the materializer's gate ORDER: status and direction are gated
    // BEFORE the basis, so a null that REACHES the basis gate is, by construction, a pre-214 directional
    // record (skip reason TrajectoryBasisNotRecorded). It is never defaulted to Supported, never re-derived
    // on read, and enters no id, cohort key, cache key or fingerprint. It DOES decide whether the judgment
    // can become a scoring signal: the materializer ALLOWLISTS Supported and (spec 215) ReferenceSupported.
    NewsTrajectoryBasis? TrajectoryBasis = null,
    // Spec 215 §2: the PROJECTED reference set the judge was handed — ids only (the ledger resolves them);
    // the projection caps' COUNTED remainder; and the reference ids the judge CITED as the trajectory's
    // comparison basis. All three TRAILING and NULLABLE: `null` = a pre-215 record (not recorded) or an
    // attempt that never assembled an input; an EMPTY ReferenceIds list on a v6 record means the ledger
    // projected nothing for the supplied statements (a measured none); an empty TrajectoryReferenceIds on a
    // Judged v6 record means the judge cited no reference. They enter no id or fingerprint; the projected
    // ids DO enter the family-set hash (NewsJudgmentInputBuilder) when non-empty, which is what makes a
    // grown ledger a re-judgment rather than a cache reuse.
    IReadOnlyList<Guid>? ReferenceIds = null,
    int? ReferenceValuesOmitted = null,
    IReadOnlyList<Guid>? TrajectoryReferenceIds = null,
    // Spec 216 §5 — the reference POLICY token the projection read under, and the KIND of every projected
    // and every cited reference. All TRAILING and NULLABLE: `null` = a pre-216 record (not recorded), or
    // an attempt that never assembled an input. An EMPTY list on a v7 record is a measured none.
    // `ReferencePolicy` is the ledger's ReportedMetricsPolicy.Version, so a reader can tell WHICH
    // verification rule admitted the figures a judgment was compared against.
    string? ReferencePolicy = null,
    IReadOnlyList<NewsJudgmentReferenceRef>? ReferenceKinds = null,
    IReadOnlyList<NewsJudgmentReferenceRef>? TrajectoryReferenceKinds = null,
    // Spec 216 §1 — the projection's counted EXCLUSIONS, so a judgment handed no reference can say why.
    // `null` = not recorded (pre-216, or no input assembled); a 0 on a v7 record is a measured zero.
    int? ReferencesExcludedNewest = null,
    int? ReferencesExcludedLaterThanFact = null,
    int? ReferencesSkippedSupersededPolicy = null,
    // SPEC 219 §2 — the coverage facts: which budget assembled this input, and what the budget left out.
    // All THREE are TRAILING and NULLABLE, and `null` means NOT RECORDED (a pre-219 record, whose read was
    // always the full-budget one) — never a fabricated Full and never a fabricated 0.
    //   ReadDepth               = Full (the spec-179 §3 depth cohort) or Breadth (the spec-219 §1 universe
    //                             pass). It is recorded provenance, not a grade.
    //   FamiliesAvailable       = how many of the company's canonical families were RESOLVABLE this pass,
    //                             before the budget cut. A 0 on a v8 record is a measured none.
    //   FamiliesWithheldByBudget= FamiliesAvailable minus the supplied count. A measured 0 means the read
    //                             saw everything there was.
    // The SUPPLIED count is not a fourth field: it is `Families.Count`, which every record already carries
    // family-by-family, so a fourth field could only ever disagree with it.
    // Observational provenance: none of the three enters an id, a cohort key, the family-set hash, a marker
    // DECISION (the marker POLICY reads ReadDepth only to render the bound honestly), a score or any
    // fingerprint. What IS hashed is the coverage POLICY VERSION, at configuration time
    // (NewsJudgmentCoveragePolicy.Version, spec 219 §6) — never these per-record values.
    NewsJudgmentReadDepth? ReadDepth = null,
    int? FamiliesAvailable = null,
    int? FamiliesWithheldByBudget = null)
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
    /// <para>
    /// <b>Spec 197 §2.2 moves it to <c>v4</c></b> for <see cref="FactIdPrefixExpansionCount"/>. The bump is
    /// owed on the "changes what a record MEANS" test that granted v2 and v3: a v4 record's validated
    /// citation set may contain ids the model never spelled in full, recovered by the shared citation
    /// resolver, and a reader must be able to tell a v3 record — where that recovery was IMPOSSIBLE, so
    /// every persisted citation was quoted completely — from a v4 record that measured an honest 0. Every
    /// pre-v4 record stays readable, is never rewritten, and hydrates the new field as <c>null</c> = NOT
    /// RECORDED (AD-8).
    /// </para>
    /// <para>
    /// <b>Spec 214 §2 moves it to <c>v5</c></b> for <see cref="TrajectoryBasis"/> and the per-family
    /// <see cref="NewsJudgmentFamilyRef.ComparisonBasis"/>. The bump is owed on the same "changes what a
    /// record MEANS" test: whether a persisted directional judgment can become a scoring signal now depends
    /// on its basis, and a reader must be able to tell a v4 record — where the basis was never computed, so
    /// the materializer counts it <c>TrajectoryBasisNotRecorded</c> — from a v5 record that recorded one.
    /// Every pre-v5 record stays readable, is never rewritten, and hydrates both fields as <c>null</c>
    /// (AD-8).
    /// </para>
    /// <para>
    /// <b>Spec 215 §2 moves it to <c>v6</c></b> for <see cref="ReferenceIds"/>,
    /// <see cref="ReferenceValuesOmitted"/>, <see cref="TrajectoryReferenceIds"/> and the per-finding
    /// <see cref="NewsJudgmentValidatedFinding.ReferenceIds"/>. The bump is owed on the same "changes what a
    /// record MEANS" test: the basis vocabulary a v6 record may carry is WIDER (<c>ReferenceSupported</c>
    /// is a value a v5 record could never hold), and a v6 directional record's basis may rest on a cited
    /// reference set that a v5 reader has no field for — so a reader must be able to tell a v5 record,
    /// where no reference could have been supplied or cited, from a v6 record that measured an honest
    /// empty set. Every pre-v6 record stays readable, is never rewritten, and hydrates the new fields as
    /// <c>null</c> (AD-8).
    /// </para>
    /// <para>
    /// <b>Spec 216 §5 moves it to <c>v7</c></b> for <see cref="ReferencePolicy"/>,
    /// <see cref="ReferenceKinds"/>, <see cref="TrajectoryReferenceKinds"/>, the three projection exclusion
    /// counts and the per-family <see cref="NewsJudgmentFamilyRef.ObservedAtUtc"/>. It earns the bump on
    /// the same "changes what a record MEANS" test: a v6 record's reference set could contain the CURRENT
    /// value of the very metric the fact quoted (that is the defect spec 216 §1 closes), while a v7
    /// record's cannot — so "this judgment cited a reference" means something different under the two, and
    /// a reader must be able to tell them apart. Every pre-v7 record stays readable, is never rewritten,
    /// and hydrates the new fields as <c>null</c> (AD-8).
    /// </para>
    /// <para>
    /// <b>Spec 219 §2 moves it to <c>v8</c></b> for <see cref="ReadDepth"/>,
    /// <see cref="FamiliesAvailable"/> and <see cref="FamiliesWithheldByBudget"/>. It earns the bump on the
    /// same "changes what a record MEANS" test as v2–v7, and here the test is at its sharpest: EVERY v7
    /// record was a full-budget read of one of ~19 rank-selected companies, while a v8 record may be a
    /// BOUNDED five-family read of any company in the universe. "No challenge found in supplied facts" is a
    /// materially weaker statement under a bounded read than under a complete one, so a reader must be able
    /// to tell the two apart — and under v7 there is no field that could. Every pre-v8 record stays
    /// readable, is never rewritten, and hydrates the three new fields as <c>null</c> = NOT RECORDED (AD-8);
    /// a pre-219 record's read was in fact always the full budget, but the record does not SAY so, and
    /// inferring it would be a fabricated measurement.
    /// </para>
    /// </summary>
    public const string CurrentSchemaVersion = "news-judgment-v8";

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
