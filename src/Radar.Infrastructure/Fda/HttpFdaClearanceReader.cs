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
/// <para>
/// MATERIALITY FILTER (spec 135). Only <b>materially meaningful regulatory events</b> are counted. Every
/// 510(k) row counts — a 510(k) IS the marketing authorisation. A PMA row counts only when it is an
/// <b>original</b> approval (<c>supplement_number</c> is an EMPTY STRING) or a <c>Panel Track</c> supplement
/// (the type that carries a NEW INDICATION). Every other supplement type — 30-day notices, real-time process
/// changes, special/immediate-track labeling changes, normal-180-day tracks — is routine post-market
/// paperwork on an already-approved device, not a business-trajectory event, and is excluded. An
/// <b>unrecognised</b> supplement type is excluded too (<b>fail closed</b>: a new FDA category must never
/// silently become bullish) and logged at Debug, so a genuinely material new type can be spotted and
/// deliberately added to the material side of <see cref="IsMaterialPmaEvent"/>. The excluded rows are still
/// counted and reported as <see cref="FdaClearanceResult.ExcludedSupplementCount"/> provenance.
/// </para>
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

    // PMA supplement fields (spec 135). Live-verified 2026-07-25: an ORIGINAL PMA carries supplement_number as
    // an EMPTY STRING (present, not absent and not null) and supplement_type likewise "" — so emptiness, not
    // presence, is what identifies an original.
    private const string SupplementNumberField = "supplement_number";
    private const string SupplementTypeField = "supplement_type";

    // The ONE supplement type that carries a new indication (live-verified: TMDX's Panel Tracks both read
    // "Labeling Change - Indications/instructions/shelf life/tradename"). Compared Ordinal, case-insensitive,
    // trimmed.
    private const string PanelTrackSupplementType = "Panel Track";

    private const string ResultsProperty = "results";
    private const string MetaProperty = "meta";
    private const string TotalProperty = "total";

    private const string Track510k = "510(k)";
    private const string TrackPma = "PMA";

    // A constant far-future ceiling (verified to return the same totals as today's date), so the reader needs
    // NO clock and its seam signature stays identical to the patents reader.
    private const string DecisionCeiling = "9999-12-31";

    // The openFDA empty-search 404 ("No matches found!") is a VALID no-recent-clearances result, not an error.
    // The sentinel pair (Success-typed instance returned from HttpOutcomeFetch's onStatus hook, recognized by
    // reference in the per-endpoint handling below, BEFORE the generic onHttpError maps other non-success
    // statuses to HttpError) lives in the shared EmptySearch404Sentinel — the ODP patents reader is the second
    // caller of the same mechanism (spec 134).
    private static readonly EmptySearch404Sentinel<FdaClearanceReadResult> Empty404 =
        new(FdaClearanceReadResult.Success(new FdaClearanceResult(0, [], 0, 0, 0)));

    // The PMA supplement types KNOWN to be routine post-market maintenance (live-verified against TransMedics'
    // real filing history, 2026-07-25). This is rule STRUCTURE, not a tunable magnitude, so it is a pinned code
    // constant rather than config. It is used ONLY to decide whether an exclusion warrants a Debug log: an
    // unrecognised type is excluded either way (fail closed), and the log exists so a genuinely MATERIAL new
    // FDA category can be spotted and added to the material side of IsMaterialPmaEvent.
    private static readonly IReadOnlySet<string> RoutineSupplementTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "30-Day Notice",
            "Real-Time Process",
            "Special (Immediate Track)",
            "Normal 180 Day Track",
            "Normal 180 Day Track No User Fee",
        };

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

        // merged.Count is the POST-filter material-event count; the endpoints' reported totals stay PRE-filter
        // raw API provenance, and the excluded routine supplements are carried alongside them.
        return FdaClearanceReadResult.Success(
            new FdaClearanceResult(
                merged.Count,
                merged,
                r510k.ReportedTotal,
                rPma.ReportedTotal,
                r510k.ExcludedSupplementCount + rPma.ExcludedSupplementCount));
    }

    // A single endpoint's normalized fetch outcome: the MATERIAL clearances + that endpoint's PRE-filter
    // reported total + how many routine supplements the materiality filter excluded, or a failure
    // Outcome/Detail. Success with an empty list covers an empty results array, openFDA's documented
    // empty-search 404, and a page whose every row was a routine supplement.
    private readonly record struct EndpointFetch(
        FdaReadOutcome Outcome,
        IReadOnlyList<FdaClearance> Clearances,
        int ReportedTotal,
        int ExcludedSupplementCount,
        string? Detail);

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
            onStatus: Empty404.OnStatus,
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
            if (Empty404.Matches(failure))
            {
                return new EndpointFetch(FdaReadOutcome.Success, [], 0, 0, null);
            }

            return new EndpointFetch(failure.Outcome, [], 0, 0, failure.Detail);
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
                return new EndpointFetch(FdaReadOutcome.Malformed, [], 0, 0, "unexpected root JSON shape");
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
                return new EndpointFetch(FdaReadOutcome.Malformed, [], 0, 0, "missing results array");
            }

            var parsed = ParseClearances(results, track, submissionField, deviceField, applicantName, ct);

            // The reported-total fallback stays the PRE-filter parsed row count: it is raw API provenance, so
            // the materiality filter must never shrink it.
            var reportedTotal = GetReportedTotal(document.RootElement, parsed.ParsedRowCount);

            return new EndpointFetch(
                FdaReadOutcome.Success, parsed.Material, reportedTotal, parsed.ExcludedSupplementCount, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "openFDA {Track} search for applicant '{Applicant}' returned malformed JSON; skipping.",
                track,
                applicantName);
            return new EndpointFetch(FdaReadOutcome.Malformed, [], 0, 0, "malformed JSON");
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

    // One endpoint's parsed rows: the MATERIAL clearances, the PRE-filter count of well-formed rows (the raw
    // API provenance the reported-total fallback uses), and how many of those rows the PMA materiality filter
    // excluded as routine post-market supplements. Material.Count + ExcludedSupplementCount == ParsedRowCount.
    private readonly record struct ParsedClearances(
        IReadOnlyList<FdaClearance> Material, int ParsedRowCount, int ExcludedSupplementCount);

    /// <summary>
    /// Maps each <c>results[]</c> row to a <see cref="FdaClearance"/>. Rows missing the submission number
    /// needed for provenance/dedupe, or carrying an unparseable/absent <c>decision_date</c>, are skipped
    /// rather than throwing or coercing to a min-value date (which would inflate the clearance count and hide
    /// field drift). An empty <c>results</c> array yields no clearances. Well-formed PMA rows are then passed
    /// through the spec-135 materiality filter (<see cref="IsMaterialPmaEvent"/>) and the non-material ones
    /// are counted out; 510(k) rows are all material by definition and are never filtered.
    /// </summary>
    private ParsedClearances ParseClearances(
        JsonElement results,
        string track,
        string submissionField,
        string deviceField,
        string applicantName,
        CancellationToken ct)
    {
        var isPma = string.Equals(track, TrackPma, StringComparison.Ordinal);
        var clearances = new List<FdaClearance>();
        var parsedRowCount = 0;
        var excluded = 0;

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

            // Counted BEFORE the materiality filter: this is the raw-API row count the reported-total fallback
            // reports, so filtering must not shrink it.
            parsedRowCount++;

            // 510(k) rows are the marketing authorisation itself — every one counts, with no sub-classification.
            if (isPma && !IsMaterialPmaEvent(row, applicantName))
            {
                excluded++;
                continue;
            }

            var deviceName = GetString(row, deviceField);

            clearances.Add(new FdaClearance(submissionNumber, deviceName, decisionDate.Value, track));
        }

        return new ParsedClearances(clearances, parsedRowCount, excluded);
    }

    /// <summary>
    /// Whether one PMA row is a materially meaningful regulatory event (spec 135): an ORIGINAL approval — its
    /// <c>supplement_number</c> is an EMPTY STRING, the live-verified marker, so emptiness is tested rather
    /// than presence — or a <c>Panel Track</c> supplement, the type that carries a NEW INDICATION (compared
    /// Ordinal, case-insensitive, trimmed). Everything else is routine post-market paperwork on an
    /// already-approved device and is excluded, INCLUDING an unrecognised type (<b>fail closed</b>) — which is
    /// additionally logged at Debug (the recognised routine types in <see cref="RoutineSupplementTypes"/> are
    /// expected, so they are excluded silently) so a genuinely material new FDA category can be spotted and
    /// deliberately added above.
    /// </summary>
    private bool IsMaterialPmaEvent(JsonElement row, string applicantName)
    {
        if (GetString(row, SupplementNumberField).Trim().Length == 0)
        {
            return true;
        }

        var supplementType = GetString(row, SupplementTypeField).Trim();
        if (string.Equals(supplementType, PanelTrackSupplementType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!RoutineSupplementTypes.Contains(supplementType))
        {
            _logger.LogDebug(
                "openFDA PMA row for applicant '{Applicant}' carries an unrecognised supplement_type "
                    + "'{SupplementType}'; excluding it as non-material (fail closed).",
                applicantName,
                supplementType);
        }

        return false;
    }

    // Reads meta.results.total (the endpoint's own grand total) as the metadata cross-check when it exceeds the
    // bounded page count; falls back to the PRE-filter parsed row count when the envelope field is absent. Both
    // are raw API provenance — meta.results.total's semantics are unchanged by the materiality filter, and the
    // fallback must stay pre-filter for the same reason.
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
            // Cross-check ONLY when larger: the meta total is the endpoint's grand total, so it should meet or
            // exceed the bounded page count. If openFDA ever reports a partial/incorrect total smaller than the
            // rows we actually parsed, prefer the parsed count so the metadata is never misleadingly low.
            return Math.Max(number, fallback);
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
