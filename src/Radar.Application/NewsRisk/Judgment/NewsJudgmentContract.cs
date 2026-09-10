using Radar.Application.NewsTyping;

namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The versioned identity of the stage-2 direction-judge prompt/schema contract (spec 185 §2/§3). Folded
/// into every judgment cohort key and persisted on every judgment record; folded into NO scoring
/// fingerprint. Bump <see cref="PromptVersion"/> when the judge's instruction text changes and
/// <see cref="SchemaVersion"/> when the structured result shape changes — either bump forks a NEW cohort,
/// so an incompatible judgment is never overwritten, reused or pooled.
/// </summary>
public static class NewsJudgmentContract
{
    /// <summary>
    /// Spec 187 §1 forked this to <c>v2</c>: the judge must now CITE the supplied facts that establish its
    /// <c>BusinessTrajectory</c>, and the instruction states the absence/price/marker rules the first live
    /// run showed a v1 judge violating. A forked prompt version forks the cohort key, so a v1 judgment can
    /// never be reused for, or pooled with, a v2 one.
    /// <para>
    /// <b>Spec 197 §2.2 forked it again to <c>v3</c>.</b> The instruction now states, as a rule rather than
    /// as the word "verbatim", that a citation must be the COMPLETE 36-character hyphenated FactId — never
    /// abbreviated, truncated, paraphrased or invented — for <c>TrajectoryFactIds</c> AND every finding's
    /// <c>FactIds</c>. Five of nineteen live v2 calls shortened ids to eight characters and lost their whole
    /// response to validation as a result.
    /// </para>
    /// <para>
    /// <b>Spec 214 §2 forked it to <c>v4</c>.</b> The instruction gained rule (11): a quantity stated as a
    /// LEVEL (backlog, cash, debt, headcount, capacity) establishes no direction by itself; only a
    /// <c>StatedComparison</c> or <c>Event</c> fact may be cited in <c>TrajectoryFactIds</c>, and the user
    /// message now renders each family's deterministic <c>ComparisonBasis</c> line. The 2026-09-07 Argan
    /// judgment read "backlog hits $2.5B" as Improving while the backlog had fallen 14% over the year.
    /// </para>
    /// <para>
    /// <b>Spec 215 §2 forked it to <c>v5</c>.</b> The instruction gained rule (12): reference values are the
    /// company's own prior statements of the same metric; when a supplied fact quotes a metric with a
    /// reference value, read the direction from the comparison and cite BOTH the fact and the ReferenceId;
    /// never cite a ReferenceId as a trajectory fact on its own. The user message renders the projected
    /// <c>Company-reported reference values</c> block after the families whenever there is one.
    /// </para>
    /// <para>
    /// <b>Spec 216 §1 forked it to <c>v6</c>.</b> Rule 12 now states that a reference value is the
    /// company's EARLIER statement and is never the figure a supplied fact itself quotes, and that each
    /// reference is labelled <c>prior</c> or <c>stated-prior</c>; the rendered reference line carries that
    /// label. Under v5 the projection could — and on a young ledger routinely did — hand the judge the
    /// SAME figure from the SAME release the fact came from, so a "reference-supported" comparison could
    /// be a value against itself.
    /// </para>
    /// </summary>
    public const string PromptVersion = "news-judgment-prompt-v6";

    /// <summary>
    /// Spec 187 §1 forked this to <c>v2</c>: the structured response gained <c>TrajectoryFactIds</c>, so
    /// the v1 and v2 result shapes are not interchangeable and must not share a cohort.
    /// <para>
    /// <b>Spec 197 §2.2 forked it to <c>v3</c>.</b> The JSON property SHAPE is unchanged, but a FactId's
    /// ACCEPTED GRAMMAR is part of the result schema: v3 additionally admits a unique 8-31 character
    /// hexadecimal prefix of a supplied representative FactId (<see cref="NewsJudgmentCitationResolver"/>),
    /// so a v2 and a v3 response are not judged by the same rules and must not share a cohort. Forking
    /// earns the accrued v2 failures a FRESH attempt budget under a contract that can accept their
    /// citations, and guarantees no completed-or-failed v2 attempt is reused as a v3 one.
    /// </para>
    /// <para>Spec 214 leaves it at <c>v3</c>: the response shape and the citation grammar are unchanged.</para>
    /// <para>
    /// <b>Spec 215 §2 forked it to <c>v4</c>.</b> The structured response gained
    /// <c>TrajectoryReferenceIds</c> and per-finding <c>ReferenceIds</c> (complete ids, the same
    /// copy-verbatim rule and the same prefix grammar as FactIds, resolved against the PROJECTED reference
    /// set), so a v3 and a v4 response are not the same shape and must not share a cohort.
    /// </para>
    /// </summary>
    public const string SchemaVersion = "news-judgment-schema-v4";

    /// <summary>
    /// The ONE stage-2 cohort-identity composition (spec 185 §3): judge provider + exact model id + this
    /// contract's prompt/schema versions + the FULL upstream stage-1 cohort key (extractor
    /// provider/model/prompt/schema/taxonomy) + the deterministic family-builder identity. A stage-1 change
    /// (extractor model, prompt, taxonomy) or a family-builder change is therefore a NEW stage-2 cohort BY
    /// CONSTRUCTION — never a silent reuse. The judge reader NAME is deliberately absent (the spec-179
    /// rule: display/provenance only, so renaming a reader forks no cohort).
    /// <para>
    /// Spec 214 §1 appends <c>comparison={StatementComparisonClassifier.Version}</c>: the per-family
    /// <c>ComparisonBasis</c> line is an INPUT THE MODEL SEES, so a table change is a new cohort — and,
    /// through the spec-194 §2 <c>news=</c> segment, a new <c>ScoringConfigVersion</c>.
    /// </para>
    /// <para>
    /// Spec 215 §2 appends <c>references={ReferenceValueProjector.Version}</c> on the same reasoning: the
    /// metric-phrase table and the caps decide WHICH reference values the model sees. Spec 216 §1 moves
    /// that token to <c>reference-projection-v2</c> — the ELIGIBILITY rules are part of the same identity,
    /// for exactly the same reason.
    /// </para>
    /// <para>
    /// Spec 220 §1 appends <c>ordering={NewsJudgmentFamilyOrdering.Version}</c> after <c>references=</c>, on
    /// the same reasoning again: the family ORDER decides which facts fill a BOUNDED judge's budget, so it is
    /// an input the model sees. Under the implicit <c>family-ordering-v1</c> a five-family breadth read saw the
    /// five most-syndicated families; under v2 it sees the directional ones first. A v1 and a v2 verdict over
    /// the same company were made over different facts and must not share a cohort — and, through the
    /// spec-194 §2 <c>news=</c> segment, the AI-ON <c>ScoringConfigVersion</c> moves with it. The prompt and
    /// the response schema do NOT move.
    /// </para>
    /// </summary>
    public static string CohortKey(string provider, string modelId, string stage1CohortKey) =>
        $"{provider}:{modelId}|{PromptVersion}|{SchemaVersion}|stage1={stage1CohortKey}"
            + $"|families={FactFamilyBuilder.IdentityString}"
            + $"|comparison={StatementComparisonClassifier.Version}"
            + $"|references={ReferenceValueProjector.Version}"
            + $"|ordering={NewsJudgmentFamilyOrdering.Version}";
}

/// <summary>
/// One judge reader's provenance identity (spec 185 §3, the spec-179 Readers seam applied verbatim):
/// <see cref="Name"/> is a display/provenance label only, while <see cref="Provider"/> +
/// <see cref="ModelId"/> are the cohort identity — composed with a stage-1 cohort via
/// <see cref="CohortKeyFor"/>, because one judge judges each stage-1 cohort's families as a SEPARATE
/// stage-2 cohort (cohorts never pool).
/// </summary>
public sealed record NewsJudgmentReaderIdentity(string Name, string Provider, string ModelId)
{
    public string CohortKeyFor(string stage1CohortKey) =>
        NewsJudgmentContract.CohortKey(Provider, ModelId, stage1CohortKey);
}

/// <summary>How one judge invocation failed, when it did.</summary>
public enum NewsJudgmentAnalysisFailure
{
    /// <summary>The call produced a parseable structured response (which may still fail validation).</summary>
    None = 0,

    /// <summary>The provider was unreachable/errored. Recorded per company; never blocks another judge.</summary>
    ProviderError,

    /// <summary>The provider answered but no typed response could be parsed from it.</summary>
    ParseError,
}

/// <summary>
/// What the judge receives (spec 185 §1): the company name/ticker plus the ordered canonical fact FAMILIES —
/// and, since spec 215 §2, the company-reported REFERENCE VALUES projected from the reported-metrics
/// ledger for the metrics those families name — and NOTHING else. No raw article prose, no headline, no
/// Radar score/rank/label, no price series, no future outcome, no prior judgment. Family size and
/// publisher breadth ride along as metadata the prompt states are corroboration of REPORTING, never N
/// independent facts. Enforced structurally by the judgment architecture guard test (no raw-text member
/// exists to carry prose). <see cref="References"/> is trailing and defaults to null (= none) so every
/// pre-215 construction site is unchanged; the analyzer renders the block only when it is non-empty.
/// </summary>
public sealed record NewsJudgmentAnalysisRequest(
    string CompanyName,
    string? Ticker,
    IReadOnlyList<NewsJudgmentInputFamily> Families,
    IReadOnlyList<NewsJudgmentReferenceValue>? References = null);

/// <summary>
/// One judge invocation's outcome: the raw typed response (pre-validation) or a named failure, plus the
/// bounded raw-response hash. Never throws for a provider failure; caller cancellation propagates.
/// </summary>
public sealed record NewsJudgmentAnalysisOutcome(
    NewsJudgmentAnalysisFailure Failure,
    NewsJudgmentModelResponse? Response,
    string? RawResponseHash,
    string? FailureDetail);

/// <summary>
/// Provider-neutral direction-judge seam (spec 185 §2), implemented in Infrastructure over the existing
/// <c>IChatClient</c> abstraction (AD-5 — no provider SDK outside Infrastructure). One instance is one
/// configured judge reader.
/// </summary>
public interface INewsJudgmentAnalyzer
{
    Task<NewsJudgmentAnalysisOutcome> AnalyzeAsync(NewsJudgmentAnalysisRequest request, CancellationToken ct);
}

/// <summary>One resolved judge reader: its provenance identity plus the analyzer bound to its provider/model.</summary>
public sealed record NewsJudgmentReader(NewsJudgmentReaderIdentity Identity, INewsJudgmentAnalyzer Analyzer);

/// <summary>
/// The resolved judge reader set (spec 185 §3), built by the composition root through the SAME reader
/// binder/validation classes specs 179/181 use. Uniqueness (names case-insensitively; (provider, model)
/// pairs exactly) is enforced at startup by the composition root, before this type exists.
/// </summary>
public sealed record NewsJudgmentReaderSet(IReadOnlyList<NewsJudgmentReader> Readers);
