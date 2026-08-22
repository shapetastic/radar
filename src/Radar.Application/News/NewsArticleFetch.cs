namespace Radar.Application.News;

/// <summary>
/// The CLOSED outcome vocabulary of one safe publisher-content fetch attempt (spec 177 §6). Closed on
/// purpose: a later semantic reader (spec 179) consumes these as durable facts about source availability,
/// and a free-form failure string would let outcomes appear that nothing downstream understands.
/// </summary>
public enum NewsArticleFetchOutcome
{
    /// <summary>The page was fetched from an allowlisted domain and visible text was extracted.</summary>
    Fetched = 0,

    /// <summary>The URL's host (or a redirect hop's host) is not on the operator's domain allowlist. No request was made to it.</summary>
    DomainNotAllowed,

    /// <summary>The host's <c>robots.txt</c> disallows this path for Radar's user agent. No content request was made.</summary>
    RobotsDisallowed,

    /// <summary>The URL (or a redirect hop) targets a non-public destination: non-HTTP(S) scheme, embedded user-info, loopback, private, link-local or otherwise non-public address. No request was made to it.</summary>
    UnsafeUrl,

    /// <summary>The redirect chain exceeded the explicit hop limit.</summary>
    RedirectLimit,

    /// <summary>The landing URL could not be parsed as an absolute URL at all.</summary>
    UnresolvedLandingUrl,

    /// <summary>The publisher refused access (HTTP 401/402/403) — treated as paywalled/subscription content, which Radar never circumvents.</summary>
    Paywalled,

    /// <summary>The response declared a content type outside the supported textual set.</summary>
    UnsupportedContentType,

    /// <summary>The response body exceeded the byte bound.</summary>
    TooLarge,

    /// <summary>A non-success HTTP status (other than the paywall/rate-limit statuses), an unreachable host, or a failed DNS resolution.</summary>
    HttpError,

    /// <summary>HTTP 429 — the publisher rate-limited the request. Radar does not retry.</summary>
    RateLimited,

    /// <summary>The request's own bounded deadline elapsed.</summary>
    Timeout,

    /// <summary>The page fetched but the deterministic visible-text extraction produced nothing.</summary>
    ExtractionEmpty,
}

/// <summary>
/// The durable record of one fetch attempt. Every attempt — success or failure — records the ACTUAL
/// retrieval instant and the retrieval-policy identity in force, so a later reader can always answer "what
/// did Radar try, when, and under which policy?".
/// </summary>
/// <param name="Outcome">The closed outcome.</param>
/// <param name="RetrievedAtUtc">The actual UTC instant of this attempt (never a publication or collection time).</param>
/// <param name="RedirectHops">How many explicit redirect hops were followed (0 for a direct response).</param>
/// <param name="ResolvedUrl">The final publisher URL the content came from, when known (after redirects).</param>
/// <param name="HttpStatus">The final HTTP status code, when a response was received.</param>
/// <param name="ContentType">The response's declared media type, when a response was received.</param>
/// <param name="Truncated">Whether the extracted text was cut at the declared character cap.</param>
/// <param name="ExtractorVersion">The versioned visible-text extractor identity, when extraction ran.</param>
/// <param name="ContentHash">SHA-256 (hex) of the extracted text, when extraction ran.</param>
/// <param name="BodyText">
/// The extracted visible text. Non-null ONLY for a <see cref="NewsArticleFetchOutcome.Fetched"/> outcome —
/// which structurally means an allowlisted source, because the reader refuses to request anything else —
/// under the operator's explicit storage permission (the allowlist). Every other outcome carries
/// <c>null</c>: a transient body is never retained and never implied to have been read.
/// </param>
/// <param name="RetrievalPolicy">
/// The versioned retrieval-policy identity (fetch rules + extractor version + allowlist). Changing the
/// allowlist, extractor or fetch policy changes this value on NEW records; it never edits an existing one.
/// </param>
public sealed record NewsArticleFetchResult(
    NewsArticleFetchOutcome Outcome,
    DateTimeOffset RetrievedAtUtc,
    int RedirectHops,
    string? ResolvedUrl,
    int? HttpStatus,
    string? ContentType,
    bool Truncated,
    string? ExtractorVersion,
    string? ContentHash,
    string? BodyText,
    string RetrievalPolicy);

/// <summary>
/// The safe, bounded, opt-in publisher-content reader seam (spec 177 §6). Its shipped posture is DISABLED
/// with an empty allowlist — the composed default graph registers no implementation at all. Implementations
/// live in Infrastructure (all HTTP stays there, AD-5) and must uphold the safety contract documented on
/// the concrete reader: allowlist-gated, robots-honoring, SSRF-validated on every request AND every redirect
/// hop, paced, bounded, and free of any authentication/paywall circumvention.
/// </summary>
public interface INewsArticleContentReader
{
    /// <summary>
    /// Attempts one bounded fetch of <paramref name="url"/>. Never throws for a per-URL failure — every
    /// failure mode is a typed <see cref="NewsArticleFetchOutcome"/>; caller cancellation still propagates.
    /// </summary>
    Task<NewsArticleFetchResult> FetchAsync(string url, CancellationToken ct);
}
