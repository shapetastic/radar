namespace Radar.Infrastructure.News;

/// <summary>
/// Reader-relevant options for the Google News RSS third-party market-attention source — Radar's alternative
/// to GDELT that is NOT per-IP throttled (keyless, no User-Agent required). Only the knobs THIS reader seam
/// needs live here: <see cref="MaxRecordsPerCompany"/> caps how many parsed items each company contributes,
/// and <see cref="EnglishOnly"/> is the default for whether coverage is restricted to English/US (spec 81
/// maps it onto each per-request <see cref="NewsSearchQuery.EnglishOnly"/>, which the reader honors by
/// appending the en-US locale params). The endpoint URL itself is owned solely by the reader
/// (<c>HttpNewsSearchReader</c>) — it is intentionally NOT duplicated here. Collector-level pacing now lives
/// here as <see cref="InterRequestDelay"/> (the <c>newssearch</c> collector is strictly sequential and paces
/// requests with it); the client-side title relevance filter and the <c>Radar:News</c> worker options bind
/// through to these fields. There is deliberately NO 429-retry knob and NO recency/timespan knob: Google News
/// RSS has no retry (the reader returns <c>RateLimited</c> immediately) and the endpoint exposes no recency
/// parameter.
/// </summary>
public sealed class NewsCollectorOptions
{
    /// <summary>Maximum parsed articles to collect per company per run (default 25). The reader clamps to a sane range.</summary>
    public int MaxRecordsPerCompany { get; init; } = 25;

    /// <summary>Whether to restrict coverage to English/US (default true); the reader appends the en-US locale params to the request when set.</summary>
    public bool EnglishOnly { get; init; } = true;

    /// <summary>
    /// The pause between successive per-company requests (default 1s). The collector is strictly sequential,
    /// so this paces successive reads politely. Unlike GDELT, Google News RSS is NOT per-IP throttled (spec-80
    /// verified: back-to-back keyless requests succeed), so only a small polite pace is needed. Registration
    /// fails fast when negative.
    /// </summary>
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromSeconds(1);
}
