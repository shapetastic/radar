using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// GETs a company's news query from the keyless GDELT DOC 2.0 endpoint
/// (<c>https://api.gdeltproject.org/api/v2/doc/doc</c> with <c>mode=ArtList&amp;format=json&amp;sort=DateDesc</c>
/// plus the configured <c>timespan</c>/<c>maxrecords</c>) and parses <c>articles[]</c> with
/// <c>System.Text.Json</c>. A company with no recent coverage (absent/empty <c>articles</c>), an unreachable
/// endpoint, the request's own timeout, and malformed/absent JSON are each reported as a typed failure on the
/// returned <see cref="GdeltReadResult"/> (with a warning) rather than swallowed; caller-requested
/// cancellation still throws. <b>Operationally-critical:</b> GDELT throttles hard and returns HTTP 429 on
/// back-to-back requests — a 429 is the distinct <see cref="GdeltReadOutcome.RateLimited"/> outcome, and the
/// reader owns a bounded EXPONENTIAL delayed retry (per <see cref="GdeltNewsQuery.MaxRetriesOn429"/> /
/// <see cref="GdeltNewsQuery.RetryDelay"/> as the base, doubling each retry) so the collector stays simple; after retries are exhausted it
/// still returns <see cref="GdeltReadOutcome.RateLimited"/> and never throws. All HTTP/JSON/GDELT code stays
/// in Infrastructure (AD-5). No User-Agent or key is required by the API.
/// </summary>
internal sealed class HttpGdeltNewsReader : IGdeltNewsReader
{
    private const string Endpoint = "https://api.gdeltproject.org/api/v2/doc/doc";
    private const int ApiMinRecords = 1;
    private const int ApiMaxRecords = 250;

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpGdeltNewsReader> _logger;

    public HttpGdeltNewsReader(HttpClient httpClient, ILogger<HttpGdeltNewsReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<GdeltReadResult> ReadAsync(GdeltNewsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var requestUri = BuildRequestUri(query);
        var maxRetries = Math.Max(0, query.MaxRetriesOn429);
        var attempt = 0;

        byte[] bytes;
        while (true)
        {
            try
            {
                using var response = await _httpClient
                    .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    // GDELT's aggressive throttle (published limit: 1 request / 5s per IP). Own a bounded,
                    // EXPONENTIAL delayed retry here (base, 2×base, …) so the collector stays simple; after
                    // retries are exhausted still return RateLimited (never throw). GDELT recommends a long
                    // cool-down after a 429 (≈60s then 120s), which a few-second pacing delay cannot satisfy.
                    if (attempt < maxRetries)
                    {
                        var backoff = query.RetryDelay * Math.Pow(2, attempt);
                        attempt++;
                        _logger.LogWarning(
                            "GDELT news search for '{QueryPhrase}' returned HTTP 429 (rate limited); "
                                + "retry {Attempt}/{MaxRetries} after {BackoffSeconds:0.#}s.",
                            query.QueryPhrase,
                            attempt,
                            maxRetries,
                            backoff.TotalSeconds);
                        await Task.Delay(backoff, ct).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning(
                        "GDELT news search for '{QueryPhrase}' returned HTTP 429 (rate limited) after retries; "
                            + "skipping.",
                        query.QueryPhrase);
                    return GdeltReadResult.Failure(GdeltReadOutcome.RateLimited, "HTTP 429 (rate limited)");
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "GDELT news search for '{QueryPhrase}' returned non-success status {StatusCode}; skipping.",
                        query.QueryPhrase,
                        (int)response.StatusCode);
                    return GdeltReadResult.Failure(
                        GdeltReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}");
                }

                // Materialize the body before disposing the response so parsing can happen synchronously.
                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex, "GDELT news search for '{QueryPhrase}' failed; skipping.", query.QueryPhrase);
                return GdeltReadResult.Failure(GdeltReadOutcome.Unreachable, "transport error");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller-requested cancellation must propagate so the run stops; do not hide it as a failure.
                throw;
            }
            catch (TaskCanceledException ex)
            {
                // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
                _logger.LogWarning(
                    ex, "GDELT news search for '{QueryPhrase}' timed out; skipping.", query.QueryPhrase);
                return GdeltReadResult.Failure(GdeltReadOutcome.Timeout, "request timed out");
            }
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);

            // The DOC endpoint returns a JSON object. Valid JSON with any other root shape (array, string,
            // number, …) is a bad/changed response, not a quiet company: report it as Malformed so the
            // collector does not treat the source as silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "GDELT news search for '{QueryPhrase}' returned JSON with an unexpected root kind "
                        + "{RootKind} (expected an object); skipping.",
                    query.QueryPhrase,
                    document.RootElement.ValueKind);
                return GdeltReadResult.Failure(GdeltReadOutcome.Malformed, "unexpected root JSON shape");
            }

            // An absent (or explicitly null) `articles` is a company with no recent coverage — not an error.
            // But `articles` present with any other non-array shape is a bad/changed payload: report it as
            // Malformed rather than silently counting the feed as succeeded with zero items.
            if (document.RootElement.TryGetProperty("articles", out var articles)
                && articles.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                _logger.LogWarning(
                    "GDELT news search for '{QueryPhrase}' returned an 'articles' property of unexpected kind "
                        + "{ArticlesKind} (expected an array); skipping.",
                    query.QueryPhrase,
                    articles.ValueKind);
                return GdeltReadResult.Failure(GdeltReadOutcome.Malformed, "unexpected 'articles' JSON shape");
            }

            var items = ParseArticles(document.RootElement, ct);
            return GdeltReadResult.Success(items);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "GDELT news search for '{QueryPhrase}' returned malformed JSON; skipping.", query.QueryPhrase);
            return GdeltReadResult.Failure(GdeltReadOutcome.Malformed, "malformed JSON");
        }
    }

    /// <summary>
    /// Builds the DOC <c>ArtList</c> GET URL. The phrase is sent quoted for a precise full-text match, with a
    /// <c>sourcelang:english</c> term appended when <see cref="GdeltNewsQuery.EnglishOnly"/>; the whole query
    /// value is URL-encoded. <c>maxrecords</c> is clamped to the API's 1–250 range defensively.
    /// </summary>
    private static Uri BuildRequestUri(GdeltNewsQuery query)
    {
        var phrase = query.QueryPhrase.Trim();
        var quoted = $"\"{phrase}\"";
        var fullQuery = query.EnglishOnly ? $"{quoted} sourcelang:english" : quoted;

        var maxRecords = Math.Clamp(query.MaxRecords, ApiMinRecords, ApiMaxRecords);

        var url =
            $"{Endpoint}?query={Uri.EscapeDataString(fullQuery)}&mode=ArtList&format=json&sort=DateDesc"
                + $"&timespan={Uri.EscapeDataString(query.Timespan.Trim())}&maxrecords={maxRecords}";

        return new Uri(url);
    }

    /// <summary>
    /// Maps each <c>articles[]</c> row to a <see cref="GdeltArticleItem"/>. Rows missing <c>url</c> (which
    /// makes them unattributable and un-dedupable) are skipped rather than throwing. An absent/empty
    /// <c>articles</c> array yields no items (a company with no recent coverage), which is NOT an error.
    /// </summary>
    private static IReadOnlyList<GdeltArticleItem> ParseArticles(JsonElement root, CancellationToken ct)
    {
        var items = new List<GdeltArticleItem>();

        if (!root.TryGetProperty("articles", out var articles) || articles.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var row in articles.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = GetString(row, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            items.Add(new GdeltArticleItem(
                Url: url,
                Title: GetString(row, "title"),
                Domain: GetString(row, "domain"),
                SeenDate: ParseSeenDate(GetString(row, "seendate")),
                Language: GetString(row, "language"),
                SourceCountry: GetString(row, "sourcecountry")));
        }

        return items;
    }

    /// <summary>
    /// Parses a GDELT <c>seendate</c> (exact form <c>yyyyMMddTHHmmssZ</c>, invariant culture, UTC) to a UTC
    /// instant. Returns <see langword="null"/> for an absent/unparseable value rather than throwing.
    /// </summary>
    private static DateTimeOffset? ParseSeenDate(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTime.TryParseExact(
                value,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return new DateTimeOffset(parsed, TimeSpan.Zero);
        }

        return null;
    }

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
