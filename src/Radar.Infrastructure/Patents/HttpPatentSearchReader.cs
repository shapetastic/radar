using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Patents;

/// <summary>
/// GETs an assignee-scoped granted-patent query against the PatentsView Search API
/// (<c>https://search.patentsview.org/api/v1/patent/</c>) with URL-encoded <c>q</c>/<c>f</c>/<c>o</c>
/// parameters and parses <c>patents[]</c> with <c>System.Text.Json</c>. The API key is read at RUNTIME from
/// the env var NAMED by <see cref="PatentCollectorOptions.ApiKeyEnvVar"/> and sent as the <c>X-Api-Key</c>
/// header — a blank/absent key returns <see cref="PatentSearchOutcome.MissingApiKey"/> with NO HTTP call
/// (the key value is never committed, logged, or surfaced). An assignee with no recent grants, an
/// unreachable endpoint, the request's own timeout, and malformed/absent JSON are each reported as a typed
/// failure on the returned <see cref="PatentSearchReadResult"/> (with a warning) rather than swallowed;
/// caller-requested cancellation still throws. All HTTP/JSON/PatentsView code stays in Infrastructure
/// (AD-5).
/// </summary>
internal sealed class HttpPatentSearchReader : IPatentSearchReader
{
    private const string BaseUrl = "https://search.patentsview.org/api/v1/patent/";
    private const string ApiKeyHeader = "X-Api-Key";

    // PatentsView query operators/fields, pinned as named constants (the field names evolve between API
    // versions; the grounding spike confirmed these against v1). q filters granted patents by a grant-date
    // floor AND an assignee-organization contains-match; f requests only the fields the evidence needs.
    private const string GrantDateField = "patent_date";
    private const string AssigneeOrgField = "assignees.assignee_organization";
    private const string GteOperator = "_gte";
    private const string ContainsOperator = "_contains";
    private const string AndOperator = "_and";

    private static readonly string[] RequestedFields = ["patent_id", "patent_title", "patent_date"];

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpPatentSearchReader> _logger;
    private readonly PatentCollectorOptions _options;

    public HttpPatentSearchReader(
        HttpClient httpClient, ILogger<HttpPatentSearchReader> logger, PatentCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _logger = logger;
        _options = options;
    }

    public string QueryUrl(string assigneeName, DateOnly grantFloor) => BuildRequestUrl(assigneeName, grantFloor);

    public async Task<PatentSearchReadResult> ReadAsync(
        string assigneeName, DateOnly grantFloor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assigneeName);

        // Resolve the API key from the env var NAMED by config. A blank/absent key is a clearly-logged
        // degrade with NO HTTP call — never an exception, never the key value in a log.
        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvVar) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "PatentsView search for assignee '{Assignee}' skipped: the API-key environment variable "
                    + "'{ApiKeyEnvVar}' is not set or is empty. The key value is never logged.",
                assigneeName,
                _options.ApiKeyEnvVar);
            return PatentSearchReadResult.Failure(
                PatentSearchOutcome.MissingApiKey,
                $"API-key env var '{_options.ApiKeyEnvVar}' is not set");
        }

        var url = BuildRequestUrl(assigneeName, grantFloor);

        var (failure, bytes) = await HttpOutcomeFetch.SendAsync<PatentSearchReadResult, byte[]>(
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
                    "PatentsView search for assignee '{Assignee}' returned non-success status {StatusCode}; skipping.",
                    assigneeName,
                    status);
                return PatentSearchReadResult.Failure(PatentSearchOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "PatentsView search for assignee '{Assignee}' failed; skipping.", assigneeName);
                return PatentSearchReadResult.Failure(PatentSearchOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                _logger.LogWarning(
                    ex, "PatentsView search for assignee '{Assignee}' timed out; skipping.", assigneeName);
                return PatentSearchReadResult.Failure(PatentSearchOutcome.Timeout, "request timed out");
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
            // bad/changed response, not a quiet assignee: report Malformed so the collector does not treat it
            // as silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "PatentsView search for assignee '{Assignee}' returned JSON with an unexpected root kind "
                        + "{RootKind} (expected an object); skipping.",
                    assigneeName,
                    document.RootElement.ValueKind);
                return PatentSearchReadResult.Failure(
                    PatentSearchOutcome.Malformed, "unexpected root JSON shape");
            }

            // A missing patents array is a changed/bad response (an assignee with no recent grants returns an
            // EMPTY array, which parses to Success 0 grants below).
            if (!document.RootElement.TryGetProperty("patents", out var patents)
                || patents.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "PatentsView search for assignee '{Assignee}' returned no 'patents' array; skipping.",
                    assigneeName);
                return PatentSearchReadResult.Failure(
                    PatentSearchOutcome.Malformed, "missing patents array");
            }

            var grants = ParseGrants(patents, ct);
            var apiReportedTotal = GetInt(document.RootElement, "total_hits", grants.Count);

            return PatentSearchReadResult.Success(
                new PatentSearchResult(grants.Count, apiReportedTotal, grants));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "PatentsView search for assignee '{Assignee}' returned malformed JSON; skipping.", assigneeName);
            return PatentSearchReadResult.Failure(PatentSearchOutcome.Malformed, "malformed JSON");
        }
    }

    private string BuildRequestUrl(string assigneeName, DateOnly grantFloor)
    {
        var floor = grantFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // q = {"_and":[{"_gte":{"patent_date":"<floor>"}},{"_contains":{"assignees.assignee_organization":"<name>"}}]}
        var query = new Dictionary<string, object>
        {
            [AndOperator] = new object[]
            {
                new Dictionary<string, object>
                {
                    [GteOperator] = new Dictionary<string, string> { [GrantDateField] = floor },
                },
                new Dictionary<string, object>
                {
                    [ContainsOperator] = new Dictionary<string, string> { [AssigneeOrgField] = assigneeName },
                },
            },
        };

        var options = new Dictionary<string, object> { ["size"] = _options.MaxPageSize };

        var q = JsonSerializer.Serialize(query);
        var f = JsonSerializer.Serialize(RequestedFields);
        var o = JsonSerializer.Serialize(options);

        return $"{BaseUrl}?q={Uri.EscapeDataString(q)}&f={Uri.EscapeDataString(f)}&o={Uri.EscapeDataString(o)}";
    }

    /// <summary>
    /// Maps each <c>patents[]</c> row to a <see cref="PatentGrant"/>. Rows missing the <c>patent_id</c>
    /// needed for provenance/dedupe, or carrying an unparseable/absent <c>patent_date</c>, are skipped rather
    /// than throwing or coercing to a min-value date (which would inflate the grant count and hide field drift).
    /// An empty <c>patents</c> array yields no grants (an assignee with no recent grants).
    /// </summary>
    private static IReadOnlyList<PatentGrant> ParseGrants(JsonElement patents, CancellationToken ct)
    {
        var grants = new List<PatentGrant>();

        foreach (var row in patents.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var patentId = GetString(row, "patent_id");
            if (string.IsNullOrWhiteSpace(patentId))
            {
                continue;
            }

            // An unparseable/absent grant date is skipped (like a missing patent_id) rather than coerced to
            // DateOnly.MinValue: a min-value date would inflate the grant count and silently mask response-shape
            // drift in the patent_date field.
            var grantDate = ParseGrantDate(GetString(row, "patent_date"));
            if (grantDate is null)
            {
                continue;
            }

            var title = GetString(row, "patent_title");

            grants.Add(new PatentGrant(patentId, title, grantDate.Value));
        }

        return grants;
    }

    private static DateOnly? ParseGrantDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement parent, string name, int fallback) =>
        parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : fallback;
}
