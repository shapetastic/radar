namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// A typed request the collector hands the reader for one company: the precise
/// <see cref="QueryPhrase"/> (the exact company name, sent quoted), the recent-coverage
/// <see cref="Timespan"/> (a GDELT window token such as <c>1w</c>/<c>2w</c>/<c>1m</c>), the page
/// <see cref="MaxRecords"/> (clamped to the API's 1–250 range by the collector), and
/// <see cref="EnglishOnly"/> (appends a <c>sourcelang:english</c> term to the phrase when set). The reader
/// serializes this into the fixed <c>doc/doc</c> GET query string.
/// <para>
/// Because GDELT throttles hard (HTTP 429 on back-to-back requests), the reader owns a single bounded
/// delayed retry: <see cref="MaxRetriesOn429"/> is how many times it re-issues the request after a 429 and
/// <see cref="RetryDelay"/> is the pause before each retry. Tests set <see cref="RetryDelay"/> to
/// <see cref="TimeSpan.Zero"/> so the 429 path stays instant and offline.
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

    /// <summary>The pause the reader waits before each 429 retry. Defaults to a small backoff; tests use <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}
