namespace Radar.Infrastructure.News;

/// <summary>
/// Reader-relevant options for the Google News RSS third-party market-attention source — Radar's alternative
/// to GDELT that is NOT per-IP throttled (keyless, no User-Agent required). Only the knobs THIS reader seam
/// needs live here: <see cref="MaxRecordsPerCompany"/> caps how many parsed items each company contributes,
/// and <see cref="EnglishOnly"/> is the default for whether coverage is restricted to English/US (spec 81
/// maps it onto each per-request <see cref="NewsSearchQuery.EnglishOnly"/>, which the reader honors by
/// appending the en-US locale params). The endpoint URL itself is owned solely by the reader
/// (<c>HttpNewsSearchReader</c>) — it is intentionally NOT duplicated here. Collector-level pacing,
/// sequencing, timespan windows, the client-side title relevance filter, and the <c>Radar:News</c> worker
/// options are <b>spec 81</b> — they are intentionally NOT added here so this slice stays a pure reader seam.
/// </summary>
public sealed class NewsCollectorOptions
{
    /// <summary>Maximum parsed articles to collect per company per run (default 25). The reader clamps to a sane range.</summary>
    public int MaxRecordsPerCompany { get; init; } = 25;

    /// <summary>Whether to restrict coverage to English/US (default true); the reader appends the en-US locale params to the request when set.</summary>
    public bool EnglishOnly { get; init; } = true;
}
