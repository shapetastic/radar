using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Patents;

/// <summary>
/// POSTs an assignee-scoped granted-patent query against the USPTO Open Data Portal (ODP) Patent File
/// Wrapper (PFW) Search API (<c>POST {BaseUrl}/api/v1/patent/applications/search</c>, default host
/// <c>https://api.uspto.gov</c>) with a JSON body (a grant-date <c>rangeFilters</c> floor + a first-applicant
/// name <c>q</c> match, a bounded <c>pagination</c> page, and a <c>fields</c> projection) and parses
/// <c>patentFileWrapperDataBag[]</c> with <c>System.Text.Json</c>. The API key is read at RUNTIME from the env
/// var NAMED by <see cref="PatentCollectorOptions.ApiKeyEnvVar"/> and sent as the <c>X-Api-Key</c> header — a
/// blank/absent key returns <see cref="PatentSearchOutcome.MissingApiKey"/> with NO HTTP call (the key value
/// is never committed, logged, or surfaced). An assignee with no recent grants, an unreachable endpoint, the
/// request's own timeout, and malformed/absent JSON are each reported as a typed failure on the returned
/// <see cref="PatentSearchReadResult"/> (with a warning) rather than swallowed; caller-requested cancellation
/// still throws. All HTTP/JSON/ODP code stays in Infrastructure (AD-5).
/// <para>
/// This reader was repointed off the retired PatentsView host (<c>search.patentsview.org</c>, now NXDOMAIN
/// after the 2026-03-20 ODP migration) onto the live ODP PFW Search API (spec 131). The request/response
/// field names below are PINNED FROM THE PUBLISHED ODP DOCS (the data.uspto.gov PFW Search query-spec + the
/// community <c>dlthub/uspto_odp</c> clients); LIVE field-name verification is DEFERRED to when the ID.me-gated
/// ODP key is obtained — the same posture as the sibling <see cref="Radar.Infrastructure.Trademarks"/> reader
/// (spec 130). If live verification finds the <c>applicationMetaData.</c> prefix differs, or that a
/// <c>valueTo</c> range bound is required, that is a deferred fixture+const tweak; the collector / extractor /
/// wiring do not change (per spec, so the scoring fingerprint does not move).
/// </para>
/// </summary>
internal sealed class HttpPatentSearchReader : IPatentSearchReader
{
    // The search path is a fixed constant; the host is configurable via PatentCollectorOptions.BaseUrl so a
    // future ODP host move is a config edit, not a code change.
    private const string SearchPath = "/api/v1/patent/applications/search";
    private const string ApiKeyHeader = "X-Api-Key";

    // ODP PFW Search request field/operator strings, pinned as named constants (pinned from ODP docs; live
    // field-name verification DEFERRED to the ID.me-gated key — see the class doc). The q clause matches the
    // first-applicant organization name; rangeFilters applies a one-sided grant-date FLOOR (valueFrom only —
    // the reader receives a floor, so no valueTo/"today" bound is invented); sort returns newest grants first.
    private const string FirstApplicantNameField = "applicationMetaData.firstApplicantName";
    private const string GrantDateField = "applicationMetaData.grantDate";
    private const string PatentNumberField = "applicationMetaData.patentNumber";
    private const string InventionTitleField = "applicationMetaData.inventionTitle";
    private const string SortDescending = "Desc";

    private static readonly string[] RequestedFields =
        [PatentNumberField, InventionTitleField, GrantDateField, FirstApplicantNameField];

    // ODP PFW Search response field names, pinned as named constants (see the class doc). Each grant row nests
    // its bibliographic fields under an applicationMetaData object.
    private const string ResultsProperty = "patentFileWrapperDataBag";
    private const string MetadataProperty = "applicationMetaData";
    private const string PatentNumberProperty = "patentNumber";
    private const string InventionTitleProperty = "inventionTitle";
    private const string GrantDateProperty = "grantDate";

    // Envelope-total cross-check fields, read defensively: the docs show "count", the community client reports
    // "totalNumFound"; either only feeds a metadata cross-check, so both are tried before falling back.
    private const string CountProperty = "count";
    private const string TotalNumFoundProperty = "totalNumFound";

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

    public string QueryUrl(string assigneeName, DateOnly grantFloor) => SearchEndpoint();

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
                "USPTO ODP patent search for assignee '{Assignee}' skipped: the API-key environment variable "
                    + "'{ApiKeyEnvVar}' is not set or is empty. The key value is never logged.",
                assigneeName,
                _options.ApiKeyEnvVar);
            return PatentSearchReadResult.Failure(
                PatentSearchOutcome.MissingApiKey,
                $"API-key env var '{_options.ApiKeyEnvVar}' is not set");
        }

        var endpoint = SearchEndpoint();
        var body = BuildRequestBody(assigneeName, grantFloor);

        var (failure, bytes) = await HttpOutcomeFetch.SendAsync<PatentSearchReadResult, byte[]>(
            send: c =>
            {
                // The X-Api-Key header is set per request (not on the shared client) so the key never persists
                // on the DI-registered HttpClient's default headers.
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(body),
                };
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
                return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, c);
            },
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onStatus: null,
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "USPTO ODP patent search for assignee '{Assignee}' returned non-success status {StatusCode}; skipping.",
                    assigneeName,
                    status);
                return PatentSearchReadResult.Failure(PatentSearchOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "USPTO ODP patent search for assignee '{Assignee}' failed; skipping.", assigneeName);
                return PatentSearchReadResult.Failure(PatentSearchOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                _logger.LogWarning(
                    ex, "USPTO ODP patent search for assignee '{Assignee}' timed out; skipping.", assigneeName);
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
                    "USPTO ODP patent search for assignee '{Assignee}' returned JSON with an unexpected root kind "
                        + "{RootKind} (expected an object); skipping.",
                    assigneeName,
                    document.RootElement.ValueKind);
                return PatentSearchReadResult.Failure(
                    PatentSearchOutcome.Malformed, "unexpected root JSON shape");
            }

            // A missing patentFileWrapperDataBag array is a changed/bad response (an assignee with no recent
            // grants returns an EMPTY array, which parses to Success 0 grants below).
            if (!document.RootElement.TryGetProperty(ResultsProperty, out var patents)
                || patents.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "USPTO ODP patent search for assignee '{Assignee}' returned no '{ResultsProperty}' array; skipping.",
                    assigneeName,
                    ResultsProperty);
                return PatentSearchReadResult.Failure(
                    PatentSearchOutcome.Malformed, "missing patents array");
            }

            var grants = ParseGrants(patents, ct);
            var apiReportedTotal = GetReportedTotal(document.RootElement, grants.Count);

            return PatentSearchReadResult.Success(
                new PatentSearchResult(grants.Count, apiReportedTotal, grants));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "USPTO ODP patent search for assignee '{Assignee}' returned malformed JSON; skipping.", assigneeName);
            return PatentSearchReadResult.Failure(PatentSearchOutcome.Malformed, "malformed JSON");
        }
    }

    // The search request is a POST with the query in the body, so there is no per-assignee GET URL: the stable
    // machine-provenance link is the constant search endpoint (the assignee + grant window are recorded in the
    // evidence metadata). One builder produces both the fetched target and this link so they cannot disagree.
    // Trim any trailing '/' from the configured host before joining the fixed path so a BaseUrl with (or
    // without) a trailing slash both yield a single-slash endpoint (no "https://api.uspto.gov//api/..." double
    // slash). SearchPath already begins with '/', so it supplies the sole separator.
    private string SearchEndpoint() => $"{_options.BaseUrl.TrimEnd('/')}{SearchPath}";

    private object BuildRequestBody(string assigneeName, DateOnly grantFloor)
    {
        var floor = grantFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Escape any embedded double-quote in the assignee name so it cannot break out of the quoted OpenSearch
        // phrase and silently malform the query (which would degrade the read to HttpError/Unreachable).
        var quotedAssignee = assigneeName.Replace("\"", "\\\"", StringComparison.Ordinal);

        // One-sided grant-date range (valueFrom only — the reader receives a floor, so no valueTo is invented).
        return new
        {
            q = $"{FirstApplicantNameField}:\"{quotedAssignee}\"",
            rangeFilters = new[]
            {
                new { field = GrantDateField, valueFrom = floor },
            },
            fields = RequestedFields,
            pagination = new { offset = 0, limit = _options.MaxPageSize },
            sort = new[]
            {
                new { field = GrantDateField, order = SortDescending },
            },
        };
    }

    /// <summary>
    /// Maps each <c>patentFileWrapperDataBag[]</c> row (its bibliographic fields nested under an
    /// <c>applicationMetaData</c> object) to a <see cref="PatentGrant"/>. Rows missing the
    /// <c>applicationMetaData</c> object or the <c>patentNumber</c> needed for provenance/dedupe, or carrying an
    /// unparseable/absent <c>grantDate</c>, are skipped rather than throwing or coercing to a min-value date
    /// (which would inflate the grant count and hide field drift). An empty array yields no grants (an assignee
    /// with no recent grants).
    /// </summary>
    private static IReadOnlyList<PatentGrant> ParseGrants(JsonElement patents, CancellationToken ct)
    {
        var grants = new List<PatentGrant>();

        foreach (var row in patents.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty(MetadataProperty, out var meta)
                || meta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var patentId = GetString(meta, PatentNumberProperty);
            if (string.IsNullOrWhiteSpace(patentId))
            {
                continue;
            }

            // An unparseable/absent grant date is skipped (like a missing patent number) rather than coerced to
            // DateOnly.MinValue: a min-value date would inflate the grant count and silently mask response-shape
            // drift in the grantDate field.
            var grantDate = ParseGrantDate(GetString(meta, GrantDateProperty));
            if (grantDate is null)
            {
                continue;
            }

            var title = GetString(meta, InventionTitleProperty);

            grants.Add(new PatentGrant(patentId, title, grantDate.Value));
        }

        return grants;
    }

    // Reads the endpoint's own grand total as the metadata cross-check when it exceeds the bounded page count.
    // The docs report the total as "count"; the community client reports it as "totalNumFound" — try both, then
    // fall back to the parsed count. Never report a total lower than the rows we actually parsed.
    private static int GetReportedTotal(JsonElement root, int fallback)
    {
        var reported = GetInt(root, CountProperty) ?? GetInt(root, TotalNumFoundProperty);
        return reported is { } number ? Math.Max(number, fallback) : fallback;
    }

    private static DateOnly? ParseGrantDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? GetInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
}
