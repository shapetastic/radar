namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// Options for the GDELT DOC 2.0 news collector — Radar's first third-party market-attention source. The
/// API needs no User-Agent and no key. <see cref="Timespan"/> sets the recent-coverage window (a GDELT token
/// such as <c>1w</c>/<c>2w</c>/<c>1m</c>); <see cref="MaxRecordsPerCompany"/> caps how many surviving
/// articles each company contributes per run; <see cref="EnglishOnly"/> restricts the query to
/// English-language coverage. <b>GDELT throttles hard</b>, so <see cref="InterRequestDelay"/> paces
/// successive per-company requests (the collector is strictly sequential) and <see cref="MaxRetriesOn429"/>
/// bounds the reader's delayed retry on an HTTP 429. Registration fails fast when a value would let the
/// collector run yet either collect nothing or hammer the throttle.
/// </summary>
public sealed class GdeltCollectorOptions
{
    /// <summary>Recent-coverage window as a GDELT timespan token (default <c>2w</c>). Registration fails fast when null/blank.</summary>
    public string Timespan { get; init; } = "2w";

    /// <summary>Maximum surviving (relevance-filtered, deduped) articles to collect per company per run (default 25).</summary>
    public int MaxRecordsPerCompany { get; init; } = 25;

    /// <summary>Whether to restrict the query to English-language coverage (default true).</summary>
    public bool EnglishOnly { get; init; } = true;

    /// <summary>
    /// The pause between successive per-company requests (default ~3s). The collector is strictly sequential,
    /// so this pacing keeps it under GDELT's aggressive rate limit. Registration fails fast when negative.
    /// </summary>
    public TimeSpan InterRequestDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How many times the reader re-issues a request after an HTTP 429 before giving up (default 1).</summary>
    public int MaxRetriesOn429 { get; init; } = 1;
}
