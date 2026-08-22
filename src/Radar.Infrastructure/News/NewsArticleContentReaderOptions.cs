namespace Radar.Infrastructure.News;

/// <summary>
/// Configuration for the safe publisher-content reader (spec 177 §6), validated at registration
/// (<c>AddHttpNewsArticleContentReader</c> fails fast on an empty allowlist, a non-contact-bearing
/// User-Agent, or a non-positive limit). The reader is only ever REGISTERED when
/// <c>Radar:NewsResearch:ArticleFetch:Enabled</c> is true — the shipped posture is disabled with an empty
/// allowlist, so the default graph contains no fetch capability at all.
/// </summary>
public sealed class NewsArticleContentReaderOptions
{
    /// <summary>
    /// The operator's explicit assertion that retrieval/storage is permitted for these domains: exact host
    /// names (<c>example.com</c>) matched case-insensitively, each also matching its subdomains as a suffix
    /// (<c>news.example.com</c>). EVERY requested host — the landing URL's AND every redirect hop's — must
    /// match, or the attempt ends <c>DomainNotAllowed</c> before any request is made to it.
    /// </summary>
    public required IReadOnlyList<string> AllowedDomains { get; init; }

    /// <summary>
    /// The contact-bearing User-Agent (a real name + reachable contact, e.g.
    /// <c>"Radar Research contact@example.com"</c>) sent on every request including the robots.txt fetch.
    /// Required — a publisher must be able to identify and reach the operator.
    /// </summary>
    public required string UserAgent { get; init; }

    /// <summary>Per-attempt deadline covering the whole redirect chain. Default 10 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Response-body byte bound; a larger body ends <c>TooLarge</c>. Default 2 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>
    /// Minimum spacing between successive requests to the SAME host (sequential politeness). Default 2s;
    /// deliberately NOT config-bound (it is a politeness floor, not a tuning knob) — overridable only in
    /// code, which tests use to run without real delays.
    /// </summary>
    public TimeSpan PerHostInterval { get; init; } = TimeSpan.FromSeconds(2);
}
