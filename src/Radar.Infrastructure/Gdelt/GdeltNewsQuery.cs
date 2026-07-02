namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// A typed request the collector hands the reader for one company: the precise
/// <see cref="QueryPhrase"/> (the exact company name, sent quoted), the recent-coverage
/// <see cref="Timespan"/> (a GDELT window token such as <c>1w</c>/<c>2w</c>/<c>1m</c>), the page
/// <see cref="MaxRecords"/> (clamped to the API's 1–250 range by the collector), and
/// <see cref="EnglishOnly"/> (appends a <c>sourcelang:english</c> term to the phrase when set). The reader
/// serializes this into the fixed <c>doc/doc</c> GET query string.
/// <para>
/// Because GDELT throttles hard (HTTP 429 on back-to-back requests), the reader owns a bounded, exponential
/// delayed retry: <see cref="MaxRetriesOn429"/> is how many times it re-issues the request after a 429 and
/// <see cref="RetryDelay"/> is the base wait — the reader backs off <c>base, 2×base, 4×base, …</c> per
/// successive retry (so a 60s base gives GDELT's recommended 60s/120s for two retries). Tests set
/// <see cref="RetryDelay"/> to <see cref="TimeSpan.Zero"/> so the 429 path stays instant and offline.
/// </para>
/// </summary>
internal sealed record GdeltNewsQuery(
    string QueryPhrase,
    string Timespan,
    int MaxRecords,
    bool EnglishOnly)
{
    /// <summary>How many times the reader re-issues the request after an HTTP 429 before giving up (0 = no retry).</summary>
    public int MaxRetriesOn429 { get; init; }

    /// <summary>
    /// Base wait before the first 429 retry; the reader backs off exponentially (base, 2×base, …). Defaults to
    /// a small backoff; tests use <see cref="TimeSpan.Zero"/> to keep the 429 path instant.
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}
