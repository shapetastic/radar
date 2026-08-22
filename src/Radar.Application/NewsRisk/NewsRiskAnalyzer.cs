namespace Radar.Application.NewsRisk;

/// <summary>
/// The versioned identity of the news-risk prompt/schema contract (spec 179 §5/§6). Folded into every
/// cohort key and persisted on every assessment; folded into NO scoring fingerprint. Bump
/// <see cref="PromptVersion"/> when the analyzer's instruction text changes and <see cref="SchemaVersion"/>
/// when the structured result shape changes — either bump forks a NEW cohort, so an incompatible assessment
/// is never overwritten or reused.
/// </summary>
public static class NewsRiskAnalysisContract
{
    public const string PromptVersion = "news-risk-prompt-v1";
    public const string SchemaVersion = "news-risk-schema-v1";

    /// <summary>
    /// The ONE cohort-identity composition: provider + exact model id + prompt/schema version. The reader
    /// NAME is deliberately absent (spec 179 §5 — it is display/provenance only, so renaming a reader forks
    /// no cohort), which is also why two readers resolving to the same (provider, model) pair are rejected
    /// at startup: they would share every cache key.
    /// </summary>
    public static string CohortKey(string provider, string modelId) =>
        $"{provider}:{modelId}|{PromptVersion}|{SchemaVersion}";
}

/// <summary>
/// One reader's provenance identity (spec 179 §5): <see cref="Name"/> is a display/provenance label only
/// (unique case-insensitively across the configured set), while <see cref="Provider"/> + <see cref="ModelId"/>
/// are the cohort identity.
/// </summary>
public sealed record NewsRiskReaderIdentity(string Name, string Provider, string ModelId)
{
    public string CohortKey => NewsRiskAnalysisContract.CohortKey(Provider, ModelId);
}

/// <summary>What the analyzer receives (spec 179 §5): company name/ticker plus ordered, id-labelled input text — and NOTHING else. No Radar score, rank or label, no price, no future outcome, no uncited company background.</summary>
public sealed record NewsRiskAnalysisRequest(
    string CompanyName,
    string? Ticker,
    IReadOnlyList<NewsRiskInputArticle> Articles);

/// <summary>How one analyzer invocation failed, when it did.</summary>
public enum NewsRiskAnalysisFailure
{
    /// <summary>The call produced a parseable structured response (which may still fail §6 validation).</summary>
    None = 0,

    /// <summary>The provider was unreachable/errored. Recorded per candidate; never blocks another reader.</summary>
    ProviderError,

    /// <summary>The provider answered but no typed response could be parsed from it.</summary>
    ParseError,
}

/// <summary>
/// One analyzer invocation's outcome: the raw typed response (pre-validation) or a named failure, plus the
/// bounded raw-response hash for the persistence rule of §6. Never throws for a provider failure; caller
/// cancellation propagates.
/// </summary>
public sealed record NewsRiskAnalysisOutcome(
    NewsRiskAnalysisFailure Failure,
    NewsRiskModelResponse? Response,
    string? RawResponseHash,
    string? FailureDetail);

/// <summary>
/// Provider-neutral news-risk analyzer seam (spec 179 §5), implemented in Infrastructure over the existing
/// <c>IChatClient</c> abstraction (AD-5 — no provider SDK outside Infrastructure). One instance is one
/// configured reader.
/// </summary>
public interface INewsRiskAnalyzer
{
    Task<NewsRiskAnalysisOutcome> AnalyzeAsync(NewsRiskAnalysisRequest request, CancellationToken ct);
}

/// <summary>One resolved reader: its provenance identity plus the analyzer bound to its provider/model.</summary>
public sealed record NewsRiskReader(NewsRiskReaderIdentity Identity, INewsRiskAnalyzer Analyzer);

/// <summary>
/// The resolved reader set (spec 179 §5), built by the composition root: an omitted/empty
/// <c>Radar:NewsResearch:Shadow:Readers</c> resolves to exactly one reader over the ambient <c>Radar:Ai</c>
/// provider/model. Uniqueness (names case-insensitively; (provider, model) pairs exactly) is enforced at
/// startup by the composition root, before this type exists.
/// </summary>
public sealed record NewsRiskReaderSet(IReadOnlyList<NewsRiskReader> Readers);
