using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Patents;

/// <summary>
/// POSTs an assignee-scoped granted-patent query against the USPTO Open Data Portal (ODP) Patent File
/// Wrapper (PFW) Search API (<c>POST {BaseUrl}/api/v1/patent/applications/search</c>, default host
/// <c>https://api.uspto.gov</c>) with a JSON body (a two-bound grant-date <c>rangeFilters</c> window + a
/// first-applicant name <c>q</c> match, a bounded <c>pagination</c> page, and a <c>fields</c> projection) and
/// parses <c>patentFileWrapperDataBag[]</c> with <c>System.Text.Json</c>. The API key is read at RUNTIME from
/// the env var NAMED by <see cref="PatentCollectorOptions.ApiKeyEnvVar"/> and sent as the <c>X-Api-Key</c>
/// header — a blank/absent key returns <see cref="PatentSearchOutcome.MissingApiKey"/> with NO HTTP call (the
/// key value is never committed, logged, or surfaced). An assignee with no recent grants, an unreachable
/// endpoint, the request's own timeout, and malformed/absent JSON are each reported as a typed failure on the
/// returned <see cref="PatentSearchReadResult"/> (with a warning) rather than swallowed; caller-requested
/// cancellation still throws. All HTTP/JSON/ODP code stays in Infrastructure (AD-5).
/// <para>
/// This reader was repointed off the retired PatentsView host (<c>search.patentsview.org</c>, now NXDOMAIN
/// after the 2026-03-20 ODP migration) onto the live ODP PFW Search API (spec 131), where its request/response
/// names were pinned from the published ODP docs. Those names are now LIVE-VERIFIED against
/// <c>api.uspto.gov</c> with a real ODP key (2026-07-25, spec 134): the endpoint, the <c>X-Api-Key</c> header,
/// all four <c>applicationMetaData.*</c> field names, the <c>patentFileWrapperDataBag</c>/<c>count</c>
/// envelope, the <c>fields</c> projection, <c>sort</c> order <c>Desc</c> and the 100-row <c>pagination</c>
/// ceiling are all confirmed correct. The same verification found the three behaviours encoded below:
/// <c>rangeFilters</c> is rejected with HTTP 400 UNCONDITIONALLY unless BOTH bounds are sent; HTTP 404 is
/// ODP's EMPTY-RESULT response (an empty body, not an error); and <c>firstApplicantName</c> matching is
/// TOKEN-based, so the returned rows are filtered client-side by a normalized applicant comparison.
/// </para>
/// <para>
/// Dataset caveat (recorded, deliberately not acted on): ODP PFW is an APPLICATIONS dataset keyed on the
/// APPLICANT. It carries no assignee field, so IP acquired by assignment is invisible to this reader — an
/// accepted limitation of the Neutral v1 patent-activity signal.
/// </para>
/// </summary>
internal sealed class HttpPatentSearchReader : IPatentSearchReader
{
    // The search path is a fixed constant; the host is configurable via PatentCollectorOptions.BaseUrl so a
    // future ODP host move is a config edit, not a code change.
    private const string SearchPath = "/api/v1/patent/applications/search";
    private const string ApiKeyHeader = "X-Api-Key";

    // ODP PFW Search request field/operator strings, pinned as named constants (LIVE-VERIFIED 2026-07-25 — see
    // the class doc). The q clause matches the first-applicant organization name; rangeFilters applies the
    // grant-date window; sort returns newest grants first.
    private const string FirstApplicantNameField = "applicationMetaData.firstApplicantName";
    private const string GrantDateField = "applicationMetaData.grantDate";
    private const string PatentNumberField = "applicationMetaData.patentNumber";
    private const string InventionTitleField = "applicationMetaData.inventionTitle";
    private const string SortDescending = "Desc";

    // ODP rejects a ONE-SIDED rangeFilters (valueFrom with no valueTo) with HTTP 400 unconditionally
    // (live-verified 2026-07-25 — it fails regardless of q/fields/sort/paging), so both bounds must be sent.
    // The reader only ever receives a floor, so the ceiling is a constant far-future date rather than "today":
    // that keeps the reader clock-free and is exactly the pattern HttpFdaClearanceReader.DecisionCeiling uses
    // for openFDA (both verified to return the same totals as a today-dated ceiling).
    private const string GrantDateCeiling = "9999-12-31";

    private static readonly string[] RequestedFields =
        [PatentNumberField, InventionTitleField, GrantDateField, FirstApplicantNameField];

    // ODP PFW Search response field names, pinned as named constants (see the class doc). Each grant row nests
    // its bibliographic fields under an applicationMetaData object.
    private const string ResultsProperty = "patentFileWrapperDataBag";
    private const string MetadataProperty = "applicationMetaData";
    private const string PatentNumberProperty = "patentNumber";
    private const string InventionTitleProperty = "inventionTitle";
    private const string GrantDateProperty = "grantDate";
    private const string FirstApplicantNameProperty = "firstApplicantName";

    // The envelope's own total. The live root is exactly { count, patentFileWrapperDataBag, requestIdentifier }
    // — the "totalNumFound" the community client reported does NOT exist on this API (verified 2026-07-25), so
    // no fallback reads it. This total is PRE-normalization (it counts ODP's token-matched rows, including the
    // false positives filtered out below), which is why it feeds provenance metadata ONLY and never the
    // emitted grant count.
    private const string CountProperty = "count";

    // ODP's empty result set is an HTTP 404 with an EMPTY body (live-verified 2026-07-25), NOT an error: an
    // assignee with no grants in the window must read as an honest zero, not a source failure. Routed through
    // the shared EmptySearch404Sentinel that the openFDA reader established for its own documented
    // empty-search 404 (spec 129) — the Success-typed sentinel is returned from HttpOutcomeFetch's onStatus
    // hook and recognized by reference after the fetch, so every OTHER non-2xx (400/401/5xx) still maps to
    // HttpError.
    private static readonly EmptySearch404Sentinel<PatentSearchReadResult> Empty404 =
        new(PatentSearchReadResult.Success(new PatentSearchResult(0, 0, [])));

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
            // ODP answers a genuinely empty search with 404 — intercept it as a zero-grant Success BEFORE the
            // generic onHttpError maps other non-success statuses to HttpError.
            onStatus: Empty404.OnStatus,
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
            if (Empty404.Matches(failure))
            {
                // The 404 sentinel IS the Success-typed zero-grant result, so it is returned as-is: this
                // assignee simply has no grants in the window (before this was handled, every quiet assignee
                // reported a source failure).
                _logger.LogDebug(
                    "USPTO ODP patent search for assignee '{Assignee}' matched no applications "
                        + "(HTTP 404 is ODP's empty-result response); recording zero grants.",
                    assigneeName);
            }

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

            var grants = ParseGrants(patents, NormalizeApplicantName(assigneeName), ct);
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

        // The grant-date range MUST carry both bounds — a one-sided valueFrom is an unconditional HTTP 400
        // (see GrantDateCeiling).
        return new
        {
            q = $"{FirstApplicantNameField}:\"{quotedAssignee}\"",
            rangeFilters = new[]
            {
                new { field = GrantDateField, valueFrom = floor, valueTo = GrantDateCeiling },
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
    /// <para>
    /// Rows are ALSO filtered against <paramref name="normalizedApplicant"/>: ODP's <c>firstApplicantName</c>
    /// match is token-based, so a phrase query for "Energy Recovery" also returns e.g.
    /// "General Energy Recovery Inc." (live-verified: 280 raw rows, 239 genuine). Comparing normalized names
    /// (upper-cased, all non-alphanumerics stripped) by PREFIX drops those false positives while still keeping
    /// the seed's own punctuation/whitespace spelling variants ("Mercury Systems, Inc." / "Mercury Systems Inc."
    /// / "MERCURY  SYSTEMS, INC." all normalize identically). A row whose applicant name is absent is dropped:
    /// it cannot be attributed to this company, and provenance is sacred.
    /// </para>
    /// </summary>
    private static IReadOnlyList<PatentGrant> ParseGrants(
        JsonElement patents, string normalizedApplicant, CancellationToken ct)
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

            if (!MatchesApplicant(GetString(meta, FirstApplicantNameProperty), normalizedApplicant))
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

    /// <summary>
    /// Whether a row's <c>firstApplicantName</c> belongs to the seed applicant, compared on NORMALIZED names
    /// (see <see cref="NormalizeApplicantName"/>) by prefix — so "Energy Recovery, Inc." matches the seed
    /// "Energy Recovery" but "General Energy Recovery Inc." does not.
    /// </summary>
    private static bool MatchesApplicant(string rowApplicant, string normalizedApplicant)
    {
        // A seed that normalizes to nothing (an all-punctuation token) would prefix-match everything, so it
        // degrades to "no client-side filter" rather than silently dropping every row.
        if (normalizedApplicant.Length == 0)
        {
            return true;
        }

        return NormalizeApplicantName(rowApplicant).StartsWith(normalizedApplicant, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalizes an applicant name for comparison: upper-cased, with ALL non-alphanumeric characters
    /// (punctuation AND whitespace) stripped. That collapses the spelling variants one company files under —
    /// "Mercury Systems, Inc." / "Mercury Systems Inc." / "MERCURY  SYSTEMS, INC." (double space) /
    /// "MERCURY SYSTEMS, INC" all become "MERCURYSYSTEMSINC".
    /// </summary>
    private static string NormalizeApplicantName(string value)
    {
        var normalized = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                normalized.Append(char.ToUpperInvariant(ch));
            }
        }

        return normalized.ToString();
    }

    // Reads the endpoint's own grand total ("count" — the only total the live envelope carries) as the metadata
    // cross-check, falling back to the parsed count when it is absent. Never report a total lower than the rows
    // we actually parsed. This total is PRE-normalization, so it can legitimately exceed the emitted grant
    // count; it is provenance-only and is never the count the evidence reports.
    private static int GetReportedTotal(JsonElement root, int fallback)
    {
        var reported = GetInt(root, CountProperty);
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
