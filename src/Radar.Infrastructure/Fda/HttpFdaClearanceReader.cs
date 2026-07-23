using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Fda;

/// <summary>
/// GETs an applicant-scoped device-clearance query against the openFDA device endpoints — 510(k)
/// (<c>https://api.fda.gov/device/510k.json</c>) and PMA (<c>https://api.fda.gov/device/pma.json</c>) — with a
/// URL-encoded <c>search</c> expression + bounded <c>limit</c>, and parses <c>results[]</c> with
/// <c>System.Text.Json</c>, MERGING the two endpoints' clearances into one result. openFDA needs NO API key.
/// An applicant with no recent clearances (including openFDA's documented empty-search <c>404</c>
/// "No matches found"), an unreachable endpoint, the request's own timeout, and malformed/absent JSON are each
/// reported as a typed failure on the returned <see cref="FdaClearanceReadResult"/> (with a warning) rather
/// than swallowed; caller-requested cancellation still throws. All HTTP/JSON/openFDA code stays in
/// Infrastructure (AD-5).
/// </summary>
internal sealed class HttpFdaClearanceReader : IFdaClearanceReader
{
    private const string Base510kUrl = "https://api.fda.gov/device/510k.json";
    private const string BasePmaUrl = "https://api.fda.gov/device/pma.json";

    // openFDA record field names, pinned as named constants (verified in the spec-129 reachability spike). The
    // 510(k) and PMA endpoints name their submission number and device name differently.
    private const string KNumberField = "k_number";       // 510(k) submission number
    private const string PmaNumberField = "pma_number";   // PMA submission number
    private const string DeviceNameField = "device_name"; // 510(k) device name
    private const string TradeNameField = "trade_name";   // PMA device name (its device_name is null/absent)
    private const string DecisionDateField = "decision_date";
    private const string ApplicantField = "applicant";

    private const string ResultsProperty = "results";
    private const string MetaProperty = "meta";
    private const string TotalProperty = "total";

    private const string Track510k = "510(k)";
    private const string TrackPma = "PMA";

    // A constant far-future ceiling (verified to return the same totals as today's date), so the reader needs
    // NO clock and its seam signature stays identical to the patents reader.
    private const string DecisionCeiling = "9999-12-31";

    private const int NotFoundStatus = 404;

    // The openFDA empty-search 404 ("No matches found!") is a VALID no-recent-clearances result, not an error.
    // The shared fetch ladder's onStatus hook returns a TFailure, so this Success-typed sentinel is returned
    // from onStatus and recognized by reference in the per-endpoint handling below (BEFORE the generic
    // onHttpError maps other non-success statuses to HttpError).
    private static readonly FdaClearanceReadResult EmptyEndpoint404 =
        FdaClearanceReadResult.Success(new FdaClearanceResult(0, [], 0, 0));

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpFdaClearanceReader> _logger;
    private readonly FdaCollectorOptions _options;

    public HttpFdaClearanceReader(
        HttpClient httpClient, ILogger<HttpFdaClearanceReader> logger, FdaCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _logger = logger;
        _options = options;
    }

    public string QueryUrl(string applicantName, DateOnly decisionFloor) =>
        BuildRequestUrl(Base510kUrl, applicantName, decisionFloor);

    public async Task<FdaClearanceReadResult> ReadAsync(
        string applicantName, DateOnly decisionFloor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(applicantName);

        var url510k = BuildRequestUrl(Base510kUrl, applicantName, decisionFloor);
        var urlPma = BuildRequestUrl(BasePmaUrl, applicantName, decisionFloor);

        // Query 510(k) then PMA; a hard failure on EITHER endpoint fails the whole read (the empty-search 404 is
        // NOT a hard failure — it is a valid zero-clearance result for that endpoint).
        var r510k = await FetchEndpointAsync(
            url510k, Track510k, KNumberField, DeviceNameField, applicantName, ct).ConfigureAwait(false);
        if (r510k.Outcome != FdaReadOutcome.Success)
        {
            return FdaClearanceReadResult.Failure(r510k.Outcome, r510k.Detail ?? r510k.Outcome.ToString());
        }

        var rPma = await FetchEndpointAsync(
            urlPma, TrackPma, PmaNumberField, TradeNameField, applicantName, ct).ConfigureAwait(false);
        if (rPma.Outcome != FdaReadOutcome.Success)
        {
            return FdaClearanceReadResult.Failure(rPma.Outcome, rPma.Detail ?? rPma.Outcome.ToString());
        }

        var merged = new List<FdaClearance>(r510k.Clearances.Count + rPma.Clearances.Count);
        merged.AddRange(r510k.Clearances);
        merged.AddRange(rPma.Clearances);

        return FdaClearanceReadResult.Success(
            new FdaClearanceResult(merged.Count, merged, r510k.ReportedTotal, rPma.ReportedTotal));
    }

    // A single endpoint's normalized fetch outcome: the parsed clearances + that endpoint's reported total, or
    // a failure Outcome/Detail. Success with an empty list covers both an empty results array and openFDA's
    // documented empty-search 404.
    private readonly record struct EndpointFetch(
        FdaReadOutcome Outcome, IReadOnlyList<FdaClearance> Clearances, int ReportedTotal, string? Detail);

    private async Task<EndpointFetch> FetchEndpointAsync(
        string url,
        string track,
        string submissionField,
        string deviceField,
        string applicantName,
        CancellationToken ct)
    {
        var (failure, bytes) = await HttpOutcomeFetch.GetAsync<FdaClearanceReadResult, byte[]>(
            _httpClient,
            url,
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            // The documented empty-search 404 is a valid zero-clearance result — intercept it BEFORE the generic
            // onHttpError maps other non-success statuses to HttpError.
            onStatus: status => status == NotFoundStatus ? EmptyEndpoint404 : null,
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "openFDA {Track} search for applicant '{Applicant}' returned non-success status {StatusCode}; skipping.",
                    track,
                    applicantName,
                    status);
                return FdaClearanceReadResult.Failure(FdaReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "openFDA {Track} search for applicant '{Applicant}' failed; skipping.", track, applicantName);
                return FdaClearanceReadResult.Failure(FdaReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                _logger.LogWarning(
                    ex, "openFDA {Track} search for applicant '{Applicant}' timed out; skipping.", track, applicantName);
                return FdaClearanceReadResult.Failure(FdaReadOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            // The empty-search 404 sentinel is a Success-with-zero-clearances for THIS endpoint.
            if (ReferenceEquals(failure, EmptyEndpoint404))
            {
                return new EndpointFetch(FdaReadOutcome.Success, [], 0, null);
            }

            return new EndpointFetch(failure.Outcome, [], 0, failure.Detail);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes!);

            // The endpoint always returns a JSON object. Any other root shape is a bad/changed response, not a
            // quiet applicant: report Malformed so the collector does not treat it as silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "openFDA {Track} search for applicant '{Applicant}' returned JSON with an unexpected root kind "
                        + "{RootKind} (expected an object); skipping.",
                    track,
                    applicantName,
                    document.RootElement.ValueKind);
                return new EndpointFetch(FdaReadOutcome.Malformed, [], 0, "unexpected root JSON shape");
            }

            // A missing results array is a changed/bad response (openFDA reports a genuinely empty search as a
            // 404 handled above, not as an empty array here).
            if (!document.RootElement.TryGetProperty(ResultsProperty, out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "openFDA {Track} search for applicant '{Applicant}' returned no 'results' array; skipping.",
                    track,
                    applicantName);
                return new EndpointFetch(FdaReadOutcome.Malformed, [], 0, "missing results array");
            }

            var clearances = ParseClearances(results, track, submissionField, deviceField, ct);
            var reportedTotal = GetReportedTotal(document.RootElement, clearances.Count);

            return new EndpointFetch(FdaReadOutcome.Success, clearances, reportedTotal, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "openFDA {Track} search for applicant '{Applicant}' returned malformed JSON; skipping.",
                track,
                applicantName);
            return new EndpointFetch(FdaReadOutcome.Malformed, [], 0, "malformed JSON");
        }
    }

    private string BuildRequestUrl(string baseUrl, string applicantName, DateOnly decisionFloor)
    {
        var floor = decisionFloor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // search=applicant:<name> AND decision_date:[<floor> TO 9999-12-31] — the WHOLE expression is
        // URL-encoded (verified: %3A colon, %20 spaces, %5B/%5D brackets all accepted).
        var search =
            $"{ApplicantField}:{applicantName} AND {DecisionDateField}:[{floor} TO {DecisionCeiling}]";

        return $"{baseUrl}?search={Uri.EscapeDataString(search)}&limit={_options.MaxPageSize.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Maps each <c>results[]</c> row to a <see cref="FdaClearance"/>. Rows missing the submission number
    /// needed for provenance/dedupe, or carrying an unparseable/absent <c>decision_date</c>, are skipped
    /// rather than throwing or coercing to a min-value date (which would inflate the clearance count and hide
    /// field drift). An empty <c>results</c> array yields no clearances.
    /// </summary>
    private static IReadOnlyList<FdaClearance> ParseClearances(
        JsonElement results, string track, string submissionField, string deviceField, CancellationToken ct)
    {
        var clearances = new List<FdaClearance>();

        foreach (var row in results.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var submissionNumber = GetString(row, submissionField);
            if (string.IsNullOrWhiteSpace(submissionNumber))
            {
                continue;
            }

            // An unparseable/absent decision date is skipped (like a missing submission number) rather than
            // coerced to DateOnly.MinValue: a min-value date would inflate the count and mask field drift.
            var decisionDate = ParseDecisionDate(GetString(row, DecisionDateField));
            if (decisionDate is null)
            {
                continue;
            }

            var deviceName = GetString(row, deviceField);

            clearances.Add(new FdaClearance(submissionNumber, deviceName, decisionDate.Value, track));
        }

        return clearances;
    }

    // Reads meta.results.total (the endpoint's own grand total) as the metadata cross-check when it exceeds the
    // bounded page count; falls back to the parsed count when the envelope field is absent.
    private static int GetReportedTotal(JsonElement root, int fallback)
    {
        if (root.TryGetProperty(MetaProperty, out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty(ResultsProperty, out var metaResults)
            && metaResults.ValueKind == JsonValueKind.Object
            && metaResults.TryGetProperty(TotalProperty, out var total)
            && total.ValueKind == JsonValueKind.Number
            && total.TryGetInt32(out var number))
        {
            return number;
        }

        return fallback;
    }

    private static DateOnly? ParseDecisionDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
