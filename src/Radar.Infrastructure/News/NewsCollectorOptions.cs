namespace Radar.Infrastructure.News;

/// <summary>
/// Reader-relevant options for the Google News RSS third-party market-attention source — Radar's alternative
/// to GDELT that is NOT per-IP throttled (keyless, no User-Agent required). Only the knobs THIS reader seam
/// needs live here: <see cref="MaxRecordsPerCompany"/> caps how many parsed items each company contributes,
/// and <see cref="EnglishOnly"/> reflects the endpoint's English/US locale pinning. Collector-level pacing,
/// sequencing, timespan windows, the client-side title relevance filter, and the <c>Radar:News</c> worker
/// options are <b>spec 81</b> — they are intentionally NOT added here so this slice stays a pure reader seam.
/// </summary>
public sealed class NewsCollectorOptions
{
    /// <summary>Maximum parsed articles to collect per company per run (default 25). The reader clamps to a sane range.</summary>
    public int MaxRecordsPerCompany { get; init; } = 25;

    /// <summary>Whether to restrict the query to English-language coverage (default true). The endpoint's locale params pin en-US.</summary>
    public bool EnglishOnly { get; init; } = true;

    /// <summary>
    /// The Google News RSS search endpoint template. The reader substitutes the URL-encoded query phrase for
    /// <c>{0}</c>. Defaults to the verified keyless endpoint; overridable only for testing/mirrors.
    /// </summary>
    public string EndpointTemplate { get; init; } =
        "https://news.google.com/rss/search?q={0}&hl=en-US&gl=US&ceid=US:en";
}
