
using Microsoft.Extensions.Logging;

using Radar.Application.Evidence;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Fetches an SEC EDGAR filing's <c>{accession}-index.html</c> page over HTTP, parses its document table
/// with BCL regex (no HTML-parser package, per spec 38), selects the earnings-release exhibit — the row
/// whose Type is exactly <c>EX-99.1</c>, else an <c>EX-99.*</c> fallback (largest by Size, then document
/// order), never the boilerplate primary 8-K — fetches that exhibit, and returns its body as plain text
/// produced by the shared <see cref="IEvidenceNormalizer"/> stripper. A quiet or unreachable filing never
/// crashes the run: non-success status, transport errors, the request's own timeout, a missing exhibit,
/// and an unparseable index are each reported as a typed failure on the returned
/// <see cref="SecEarningsReleaseReadResult"/> (with a warning) rather than swallowed; caller-requested
/// cancellation still throws. A 403 is called out distinctly because SEC returns it when the mandatory
/// <c>User-Agent</c> is missing/invalid. All HTTP/HTML/SEC code stays in Infrastructure (AD-5).
/// <para>
/// To reduce the sustained www.sec.gov footprint that gets the IP fair-access-flagged (spec 107), the reader
/// PACES its requests: before each actual network fetch it waits so successive requests are at least
/// <see cref="SecEarningsReleaseReaderOptions.MinRequestInterval"/> apart (driven by the injected
/// <see cref="TimeProvider"/> so it is deterministic under test). A <see cref="TimeSpan.Zero"/> interval
/// restores the un-paced behaviour.
/// </para>
/// </summary>
internal sealed class HttpSecEarningsReleaseReader : ISecEarningsReleaseReader
{
    private readonly HttpClient _httpClient;
    private readonly IEvidenceNormalizer _normalizer;
    private readonly SecEarningsReleaseReaderOptions _retryOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpSecEarningsReleaseReader> _logger;

    // The instant of the reader's last actual www.sec.gov request, used to pace successive requests at least
    // MinRequestInterval apart (spec 107). Null until the first request is issued.
    private DateTimeOffset? _lastRequestAtUtc;

    public HttpSecEarningsReleaseReader(
        HttpClient httpClient,
        IEvidenceNormalizer normalizer,
        SecEarningsReleaseReaderOptions retryOptions,
        TimeProvider timeProvider,
        ILogger<HttpSecEarningsReleaseReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _normalizer = normalizer;
        _retryOptions = retryOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cik);
        ArgumentException.ThrowIfNullOrWhiteSpace(accession);

        // Same URL facts the spec-56 reader computes: CIK with leading zeros stripped, accession with dashes
        // removed in the path (but the dashed accession is kept in the index filename).
        var cikTrim = cik.Trim();
        var accTrim = accession.Trim();
        var baseUrl = SecEdgarUrls.BuildArchiveBaseUrl(cikTrim, accTrim);
        var indexUrl = SecEdgarUrls.BuildIndexUrl(cikTrim, accTrim, ".html");

        var (indexFailure, indexBody) = await FetchAsync(indexUrl, ct).ConfigureAwait(false);
        if (indexFailure is not null)
        {
            return indexFailure;
        }

        var rows = ParseDocumentTable(indexBody);
        if (rows.Count == 0)
        {
            _logger.LogWarning(
                "SEC filing index {IndexUrl} had no parseable document table; skipping earnings-release read.",
                indexUrl);
            return SecEarningsReleaseReadResult.Failure(
                SecEarningsReleaseReadOutcome.Malformed, "no parseable document table");
        }

        var selected = SelectEarningsExhibit(rows);
        if (selected is null)
        {
            // The index parsed fine but carries no EX-99.* exhibit; never fall back to the boilerplate 8-K.
            _logger.LogInformation(
                "SEC filing index {IndexUrl} carried no EX-99.* earnings-release exhibit; skipping.", indexUrl);
            return SecEarningsReleaseReadResult.Failure(
                SecEarningsReleaseReadOutcome.NoEarningsExhibit, "no EX-99.* exhibit row");
        }

        var exhibitUrl = $"{baseUrl}/{selected.FileName}";

        var (exhibitFailure, exhibitBody) = await FetchAsync(exhibitUrl, ct).ConfigureAwait(false);
        if (exhibitFailure is not null)
        {
            return exhibitFailure;
        }

        // Reuse the shared HTML stripper (spec 38) — tag-stripped, entity-decoded, whitespace-collapsed plain
        // text. The normalizer also computes a content hash we ignore here; reuse over a second stripper.
        var plainText = _normalizer.Normalize(title: null, rawText: exhibitBody).NormalizedText;

        // SelectEarningsExhibit only returns EX-99 rows, whose Type is guaranteed non-null.
        return SecEarningsReleaseReadResult.Success(plainText, selected.Type!, selected.FileName);
    }

    /// <summary>
    /// Fetches a URL as a string, mapping SEC's HTTP outcomes to typed failures exactly as
    /// <see cref="HttpSecFilingReader"/> does. Returns a non-null failure (and empty body) on any bad
    /// response; caller-requested cancellation re-throws. An HTTP 429 (rate limited — SEC returns it under the
    /// Archives burst this reader fires) is not skipped straight away: this method owns a bounded exponential
    /// backoff-retry (per <see cref="SecEarningsReleaseReaderOptions"/>, mirroring GDELT) and only returns a
    /// typed <see cref="SecEarningsReleaseReadOutcome.RateLimited"/> failure once retries are exhausted — it
    /// never throws on 429. Because both call sites (index and exhibit) route through here, both get the retry.
    /// </summary>
    private async Task<(SecEarningsReleaseReadResult? Failure, string Body)> FetchAsync(
        string url, CancellationToken ct)
    {
        var maxRetries = Math.Max(0, _retryOptions.MaxRetriesOn429);
        var attempt = 0;

        while (true)
        {
            // Honour caller cancellation before each (sequential) request, independent of transport timing.
            ct.ThrowIfCancellationRequested();

            // Pace the www.sec.gov footprint (spec 107): wait so this request is at least MinRequestInterval after
            // the previous one. Retries are paced too (inside this loop). MinRequestInterval == Zero ⇒ no delay
            // (parity). The TimeProvider overload makes a FakeTimeProvider drive the delay deterministically, and
            // Task.Delay honours ct.
            var minInterval = _retryOptions.MinRequestInterval;
            if (minInterval > TimeSpan.Zero && _lastRequestAtUtc is { } last)
            {
                var remaining = minInterval - (_timeProvider.GetUtcNow() - last);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _timeProvider, ct).ConfigureAwait(false);
                }
            }

            _lastRequestAtUtc = _timeProvider.GetUtcNow();

            var (failure, body) = await SecHttpFetch.GetAsync<SecEarningsReleaseReadResult, string>(
                _httpClient,
                url,
                readBody: (content, c) => content.ReadAsStringAsync(c),
                onForbidden: () =>
                {
                    _logger.LogWarning(
                        "SEC {Url} returned HTTP 403 Forbidden; this is almost always a missing or invalid "
                            + "User-Agent (SEC requires a compliant 'Radar Research <email>' UA). Skipping.",
                        url);
                    return SecEarningsReleaseReadResult.Failure(
                        SecEarningsReleaseReadOutcome.Forbidden, "HTTP 403 (User-Agent)");
                },
                onHttpError: status =>
                {
                    _logger.LogWarning(
                        "SEC {Url} returned non-success status {StatusCode}; skipping.",
                        url,
                        status);
                    return SecEarningsReleaseReadResult.Failure(
                        SecEarningsReleaseReadOutcome.HttpError, $"HTTP {status}");
                },
                onUnreachable: ex =>
                {
                    _logger.LogWarning(ex, "SEC {Url} fetch failed; skipping.", url);
                    return SecEarningsReleaseReadResult.Failure(
                        SecEarningsReleaseReadOutcome.Unreachable, "transport error");
                },
                onTimeout: ex =>
                {
                    // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
                    _logger.LogWarning(ex, "SEC {Url} fetch timed out; skipping.", url);
                    return SecEarningsReleaseReadResult.Failure(
                        SecEarningsReleaseReadOutcome.Timeout, "request timed out");
                },
                ct,
                // Map 429 to RateLimited WITHOUT logging here: the retry loop below owns the retry-vs-final
                // wording (and the log-before-delay ordering), which needs the attempt state.
                onRateLimited: () => SecEarningsReleaseReadResult.Failure(
                    SecEarningsReleaseReadOutcome.RateLimited, "HTTP 429 (rate limited)")).ConfigureAwait(false);

            if (failure is null)
            {
                return (null, body ?? string.Empty);
            }

            if (failure.Outcome != SecEarningsReleaseReadOutcome.RateLimited)
            {
                return (failure, string.Empty);
            }

            // SEC's fair-access limit 429s the Archives burst this reader fires. Own a bounded, EXPONENTIAL
            // delayed retry here (base, 2×base, …) so a transient throttle stops starving the AI directional
            // path; after retries are exhausted still return RateLimited (never throw).
            if (attempt < maxRetries)
            {
                var backoff = ExponentialBackoff.Compute(_retryOptions.RetryBackoff, attempt);
                attempt++;
                _logger.LogWarning(
                    "SEC {Url} returned HTTP 429 (rate limited); retry {Attempt}/{MaxRetries} after {BackoffSeconds:0.#}s.",
                    url,
                    attempt,
                    maxRetries,
                    backoff.TotalSeconds);
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                continue;
            }

            _logger.LogWarning(
                "SEC {Url} returned HTTP 429 (rate limited) after retries; skipping.",
                url);
            return (failure, string.Empty);
        }
    }

    /// <summary>
    /// SPEC 217 — the index-table parse and the EX-99 selection now live in the SHARED
    /// <see cref="SecFilingIndexTable"/> (reuse over copy): the item-1.01 acquisition reader needs the
    /// identical row/cell/size rules, and a second copy would drift. Behaviour here is unchanged.
    /// </summary>
    private static List<SecFilingIndexRow> ParseDocumentTable(string html) =>
        SecFilingIndexTable.Parse(html);

    /// <inheritdoc cref="SecFilingIndexTable.SelectEarningsExhibit"/>
    private static SecFilingIndexRow? SelectEarningsExhibit(List<SecFilingIndexRow> rows) =>
        SecFilingIndexTable.SelectEarningsExhibit(rows);
}
