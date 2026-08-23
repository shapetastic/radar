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
    public const string PromptVersion = "news-judgment-prompt-v1";
    public const string SchemaVersion = "news-judgment-schema-v1";

    /// <summary>
    /// The ONE stage-2 cohort-identity composition (spec 185 §3): judge provider + exact model id + this
    /// contract's prompt/schema versions + the FULL upstream stage-1 cohort key (extractor
    /// provider/model/prompt/schema/taxonomy) + the deterministic family-builder identity. A stage-1 change
    /// (extractor model, prompt, taxonomy) or a family-builder change is therefore a NEW stage-2 cohort BY
    /// CONSTRUCTION — never a silent reuse. The judge reader NAME is deliberately absent (the spec-179
    /// rule: display/provenance only, so renaming a reader forks no cohort).
    /// </summary>
    public static string CohortKey(string provider, string modelId, string stage1CohortKey) =>
        $"{provider}:{modelId}|{PromptVersion}|{SchemaVersion}|stage1={stage1CohortKey}"
            + $"|families={FactFamilyBuilder.IdentityString}";
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
/// and NOTHING else. No raw article prose, no headline, no Radar score/rank/label, no price series, no
/// future outcome, no prior judgment. Family size and publisher breadth ride along as metadata the prompt
/// states are corroboration of REPORTING, never N independent facts. Enforced structurally by the judgment
/// architecture guard test (no raw-text member exists to carry prose).
/// </summary>
public sealed record NewsJudgmentAnalysisRequest(
    string CompanyName,
    string? Ticker,
    IReadOnlyList<NewsJudgmentInputFamily> Families);

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
