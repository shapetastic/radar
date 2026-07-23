using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Trademarks;

/// <summary>
/// GETs an owner-scoped trademark-application query against the USPTO trademark search route and parses the
/// results array with <c>System.Text.Json</c>. The API key is read at RUNTIME from the env var NAMED by
/// <see cref="TrademarkCollectorOptions.ApiKeyEnvVar"/> and sent as the <c>X-Api-Key</c> header — a
/// blank/absent key returns <see cref="TrademarkSearchOutcome.MissingApiKey"/> with NO HTTP call (the key
/// value is never committed, logged, or surfaced). An owner with no recent filings, an unreachable endpoint,
/// the request's own timeout, and malformed/absent JSON are each reported as a typed failure on the returned
/// <see cref="TrademarkSearchReadResult"/> (with a warning) rather than swallowed; caller-requested
/// cancellation still throws. All HTTP/JSON code stays in Infrastructure (AD-5).
/// <para>
/// The endpoint (<c>https://api.uspto.gov/api/v1/trademark/applications/search</c>) and the request/response
/// field names below are pinned from the USPTO Open Data Portal trademark docs; the spec-130 reachability
/// spike confirmed the route requires <c>USPTO_API_KEY</c> (it returns <c>Missing Authentication Token</c>
/// without a key). Live field-name verification is DEFERRED to when the ID.me-gated ODP key is obtained —
/// the same posture as spec 131 (patents ODP migration). If the live schema differs, adjust this reader's
/// parse + the offline fixtures only; the collector/extractor/wiring do not change (per spec).
/// </para>
/// </summary>
internal sealed class HttpTrademarkSearchReader : ITrademarkSearchReader
{
    private const string BaseUrl = "https://api.uspto.gov/api/v1/trademark/applications/search";
    private const string ApiKeyHeader = "X-Api-Key";

    // USPTO trademark search request operators, pinned as named constants (verified route; live field names
    // deferred to the ID.me-gated key — see the class doc). q filters applications by an owner-name match AND a
    // filing-date floor; rows bounds the single page.
    private const string QueryParam = "q";
    private const string RowsParam = "rows";
    private const string OwnerField = "owner";
    private const string FilingDateField = "filingDate";

    // USPTO trademark search response field names, pinned as named constants (see the class doc).
    private const string ResultsProperty = "results";
    private const string TotalProperty = "count";
    private const string SerialNumberField = "serialNumber";
    private const string MarkTextField = "markText";

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpTrademarkSearchReader> _logger;
    private readonly TrademarkCollectorOptions _options;

    public HttpTrademarkSearchReader(
        HttpClient httpClient, ILogger<HttpTrademarkSearchReader> logger, TrademarkCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _logger = logger;
        _options = options;
    }

    public string QueryUrl(string ownerName, DateOnly filingFloor) => BuildRequestUrl(ownerName, filingFloor);

    public async Task<TrademarkSearchReadResult> ReadAsync(
        string ownerName, DateOnly filingFloor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerName);

        // Resolve the API key from the env var NAMED by config. A blank/absent key is a clearly-logged degrade
        // with NO HTTP call — never an exception, never the key value in a log.
        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvVar) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "USPTO trademark search for owner '{Owner}' skipped: the API-key environment variable "
                    + "'{ApiKeyEnvVar}' is not set or is empty. The key value is never logged.",
                ownerName,
                _options.ApiKeyEnvVar);
            return TrademarkSearchReadResult.Failure(
                TrademarkSearchOutcome.MissingApiKey,
                $"API-key env var '{_options.ApiKeyEnvVar}' is not set");
        }

        var url = BuildRequestUrl(ownerName, filingFloor);

        var (failure, bytes) = await HttpOutcomeFetch.SendAsync<TrademarkSearchReadResult, byte[]>(
            send: c =>
            {
                // The X-Api-Key header is set per request (not on the shared client) so the key never persists
                // on the DI-registered HttpClient's default headers.
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
                return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, c);
            },
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onStatus: null,
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "USPTO trademark search for owner '{Owner}' returned non-success status {StatusCode}; skipping.",
                    ownerName,
                    status);
                return TrademarkSearchReadResult.Failure(TrademarkSearchOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "USPTO trademark search for owner '{Owner}' failed; skipping.", ownerName);
                return TrademarkSearchReadResult.Failure(TrademarkSearchOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                _logger.LogWarning(
                    ex, "USPTO trademark search for owner '{Owner}' timed out; skipping.", ownerName);
                return TrademarkSearchReadResult.Failure(TrademarkSearchOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes!);

            // The endpoint always returns a JSON object. Any other root shape (array, string, number, …) is a
            // bad/changed response, not a quiet owner: report Malformed so the collector does not treat it as
            // silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "USPTO trademark search for owner '{Owner}' returned JSON with an unexpected root kind "
                        + "{RootKind} (expected an object); skipping.",
                    ownerName,
                    document.RootElement.ValueKind);
                return TrademarkSearchReadResult.Failure(
                    TrademarkSearchOutcome.Malformed, "unexpected root JSON shape");
            }

            // A missing results array is a changed/bad response (an owner with no recent filings returns an
            // EMPTY array, which parses to Success 0 filings below).
            if (!document.RootElement.TryGetProperty(ResultsProperty, out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "USPTO trademark search for owner '{Owner}' returned no 'results' array; skipping.",
                    ownerName);
                return TrademarkSearchReadResult.Failure(
                    TrademarkSearchOutcome.Malformed, "missing results array");
            }

            var filings = ParseFilings(results, ct);
            var apiReportedTotal = GetReportedTotal(document.RootElement, filings.Count);

            return TrademarkSearchReadResult.Success(
                new TrademarkSearchResult(filings.Count, apiReportedTotal, filings));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "USPTO trademark search for owner '{Owner}' returned malformed JSON; skipping.", ownerName);
            return TrademarkSearchReadResult.Failure(TrademarkSearchOutcome.Malformed, "malformed JSON");
        }
    }

    private string BuildRequestUrl(string ownerName, DateOnly filingFloor)
    {
        var floor = filingFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // q = owner:<name> AND filingDate:[<floor> TO *] — the WHOLE expression is URL-encoded.
        var query = $"{OwnerField}:{ownerName} AND {FilingDateField}:[{floor} TO *]";

        return $"{BaseUrl}?{QueryParam}={Uri.EscapeDataString(query)}"
            + $"&{RowsParam}={_options.MaxPageSize.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Maps each <c>results[]</c> row to a <see cref="TrademarkFiling"/>. Rows missing the serial number needed
    /// for provenance/dedupe, or carrying an unparseable/absent <c>filingDate</c>, are skipped rather than
    /// throwing or coercing to a min-value date (which would inflate the filing count and hide field drift). An
    /// empty <c>results</c> array yields no filings (an owner with no recent filings).
    /// </summary>
    private static IReadOnlyList<TrademarkFiling> ParseFilings(JsonElement results, CancellationToken ct)
    {
        var filings = new List<TrademarkFiling>();

        foreach (var row in results.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var serialNumber = GetString(row, SerialNumberField);
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                continue;
            }

            // An unparseable/absent filing date is skipped (like a missing serial number) rather than coerced to
            // DateOnly.MinValue: a min-value date would inflate the filing count and silently mask response-shape
            // drift in the filingDate field.
            var filingDate = ParseFilingDate(GetString(row, FilingDateField));
            if (filingDate is null)
            {
                continue;
            }

            var markText = GetString(row, MarkTextField);

            filings.Add(new TrademarkFiling(serialNumber, markText, filingDate.Value));
        }

        return filings;
    }

    // Reads the endpoint's own grand total as the metadata cross-check when it exceeds the bounded page count;
    // falls back to the parsed count when the envelope field is absent.
    private static int GetReportedTotal(JsonElement root, int fallback)
    {
        if (root.TryGetProperty(TotalProperty, out var total)
            && total.ValueKind == JsonValueKind.Number
            && total.TryGetInt32(out var number))
        {
            // Cross-check ONLY when larger: the total is the grand total, so it should meet or exceed the bounded
            // page count. If the API ever reports a partial/incorrect total smaller than the rows we parsed,
            // prefer the parsed count so the metadata is never misleadingly low.
            return Math.Max(number, fallback);
        }

        return fallback;
    }

    private static DateOnly? ParseFilingDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
