namespace Radar.Application.NewsTyping;

/// <summary>
/// The versioned identity of the news-typing prompt/schema/taxonomy contract (spec 181 §2/§3). Folded into
/// every typing cohort key and persisted on every typing record; folded into NO scoring fingerprint. Bump
/// <see cref="PromptVersion"/> when the extractor's instruction text changes, <see cref="SchemaVersion"/>
/// when the structured result shape changes, and declare a new taxonomy version (a new enum, spec 181 §3)
/// when the event vocabulary changes — any of the three forks a NEW cohort, so an incompatible typing is
/// never overwritten, reused or pooled.
/// </summary>
public static class NewsTypingContract
{
    public const string PromptVersion = "news-typing-prompt-v1";
    public const string SchemaVersion = "news-typing-schema-v1";

    /// <summary>The taxonomy version this contract types against — a COHORT DIMENSION, not a display detail.</summary>
    public const string TaxonomyVersion = NewsEventTaxonomy.TaxonomyVersion;

    /// <summary>
    /// The ONE cohort-identity composition: provider + exact model id + prompt/schema/taxonomy version. The
    /// reader NAME is deliberately absent (the spec-179 rule: it is display/provenance only, so renaming a
    /// reader forks no cohort) — which is also why two readers resolving to the same (provider, model) pair
    /// are rejected at startup. Capture mode is NOT part of the key: it is recorded per typing record and
    /// partitions every output/artifact instead (capture-mode cohorts never pool either).
    /// </summary>
    public static string CohortKey(string provider, string modelId) =>
        $"{provider}:{modelId}|{PromptVersion}|{SchemaVersion}|{TaxonomyVersion}";
}

/// <summary>
/// One typing reader's provenance identity (spec 181 §4, the spec-179 Readers seam applied verbatim):
/// <see cref="Name"/> is a display/provenance label only, while <see cref="Provider"/> +
/// <see cref="ModelId"/> are the cohort identity.
/// </summary>
public sealed record NewsTypingReaderIdentity(string Name, string Provider, string ModelId)
{
    public string CohortKey => NewsTypingContract.CohortKey(Provider, ModelId);
}

/// <summary>How one extractor invocation failed, when it did.</summary>
public enum NewsTypingExtractionFailure
{
    /// <summary>The call produced a parseable structured response (which may still fail validation).</summary>
    None = 0,

    /// <summary>The provider was unreachable/errored. Recorded per observation; never blocks another reader.</summary>
    ProviderError,

    /// <summary>The provider answered but no typed response could be parsed from it.</summary>
    ParseError,
}

/// <summary>
/// What the extractor receives (spec 181 §2): the company ticker for context plus ONE observation's supplied
/// text — and NOTHING else. No Radar score, rank or label, no price, no future outcome, no other
/// observation (the extractor works one observation at a time and must not invent cross-observation
/// identifiers — fact families are the separate deterministic §4 pass).
/// </summary>
public sealed record NewsTypingExtractionRequest(string? Ticker, NewsTypingInputObservation Observation);

/// <summary>
/// One extractor invocation's outcome: the raw typed response (pre-validation) or a named failure, plus the
/// bounded raw-response hash. Never throws for a provider failure; caller cancellation propagates.
/// </summary>
public sealed record NewsTypingExtractionOutcome(
    NewsTypingExtractionFailure Failure,
    NewsTypingModelResponse? Response,
    string? RawResponseHash,
    string? FailureDetail);

/// <summary>
/// Provider-neutral news-typing extractor seam (spec 181 §4), implemented in Infrastructure over the
/// existing <c>IChatClient</c> abstraction (AD-5 — no provider SDK outside Infrastructure). One instance is
/// one configured reader.
/// </summary>
public interface INewsTypingExtractor
{
    Task<NewsTypingExtractionOutcome> ExtractAsync(NewsTypingExtractionRequest request, CancellationToken ct);
}

/// <summary>One resolved typing reader: its provenance identity plus the extractor bound to its provider/model.</summary>
public sealed record NewsTypingReader(NewsTypingReaderIdentity Identity, INewsTypingExtractor Extractor);

/// <summary>
/// The resolved typing reader set (spec 181 §4), built by the composition root through the SAME reader
/// binder/validation classes spec 179 uses. Uniqueness (names case-insensitively; (provider, model) pairs
/// exactly) is enforced at startup by the composition root, before this type exists.
/// </summary>
public sealed record NewsTypingReaderSet(IReadOnlyList<NewsTypingReader> Readers);
