using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Fcc;

/// <summary>
/// GETs a grantee-scoped equipment-authorization query against the FCC OET Equipment Authorization System
/// (EAS) GenericSearch CSV export (<c>https://apps.fcc.gov/oetcf/eas/reports/GenericSearchResult.cfm</c>) and
/// parses the returned CSV by HEADER NAME (robust to column reorder) with <c>System.Text.Json</c>-free plain
/// parsing. The EAS GenericSearch app is the ONLY FCC source with per-grant fields (FCC ID, grant date,
/// applicant, equipment class); the <c>opendata.fcc.gov</c> Socrata sets carry only grantee-entity
/// registrations, so they are deliberately not used. The <c>apps.fcc.gov</c> host is Akamai-gated (a datacenter
/// IP is 403'd; a residential/browser context is served), so in an unattended run a production 403 simply
/// degrades to <see cref="FccAuthOutcome.HttpError"/> — acceptable because the collector is opt-in OFF. No API
/// key is required. A grantee with no recent authorizations, an unreachable endpoint, the request's own
/// timeout, and a malformed/absent CSV are each reported as a typed failure on the returned
/// <see cref="FccAuthReadResult"/> (with a warning) rather than swallowed; caller-requested cancellation still
/// throws. All HTTP/CSV/FCC code stays in Infrastructure (AD-5).
/// </summary>
internal sealed class HttpFccAuthReader : IFccAuthReader
{
    private const string BaseUrl = "https://apps.fcc.gov/oetcf/eas/reports/GenericSearchResult.cfm";

    // EAS GenericSearch query parameters, pinned as named constants (the maintainer's live spike confirmed the
    // exact names — that is why they are isolated here). mode/calledFromFrame put the app in the plain result
    // mode; applicant_name filters by grantee/applicant org; grant_date_from/_to bound the recent window.
    private const string ModeParam = "mode";
    private const string ModeValue = "Standard";
    private const string CalledFromFrameParam = "calledFromFrame";
    private const string CalledFromFrameValue = "N";
    private const string ApplicantNameParam = "applicant_name";
    private const string GrantDateFromParam = "grant_date_from";
    private const string GrantDateToParam = "grant_date_to";

    // EAS uses US-style MM/dd/yyyy dates for BOTH the query bounds and the returned Grant Date column.
    private const string DateFormat = "MM/dd/yyyy";

    // Expected CSV header column names (parsed BY NAME so a column reorder does not break parsing). The FCC ID
    // is the Grantee Code + Product Code concatenated; the Equipment Class is the short product description.
    private const string GranteeCodeColumn = "Grantee Code";
    private const string ProductCodeColumn = "Product Code";
    private const string GrantDateColumn = "Grant Date";
    private const string EquipmentClassColumn = "Equipment Class";

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpFccAuthReader> _logger;
    private readonly FccCollectorOptions _options;
    private readonly TimeProvider _timeProvider;

    public HttpFccAuthReader(
        HttpClient httpClient,
        ILogger<HttpFccAuthReader> logger,
        FccCollectorOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _logger = logger;
        _options = options;
        _timeProvider = timeProvider;
    }

    public string QueryUrl(string granteeName, DateOnly grantFloor) => BuildRequestUrl(granteeName, grantFloor);

    public async Task<FccAuthReadResult> ReadAsync(
        string granteeName, DateOnly grantFloor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(granteeName);

        var url = BuildRequestUrl(granteeName, grantFloor);

        var (failure, body) = await HttpOutcomeFetch.GetAsync<FccAuthReadResult, string>(
            _httpClient,
            url,
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsStringAsync(c),
            onStatus: null,
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "FCC EAS search for grantee '{Grantee}' returned non-success status {StatusCode}; skipping.",
                    granteeName,
                    status);
                return FccAuthReadResult.Failure(FccAuthOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(
                    ex, "FCC EAS search for grantee '{Grantee}' failed; skipping.", granteeName);
                return FccAuthReadResult.Failure(FccAuthOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                _logger.LogWarning(
                    ex, "FCC EAS search for grantee '{Grantee}' timed out; skipping.", granteeName);
                return FccAuthReadResult.Failure(FccAuthOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        return ParseCsv(body!, granteeName, ct);
    }

    private FccAuthReadResult ParseCsv(string body, string granteeName, CancellationToken ct)
    {
        // Split on CR/LF into non-empty logical lines. EAS CSV never puts an embedded newline inside a quoted
        // field, so a line-level split before field parsing is safe for this source.
        var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            _logger.LogWarning(
                "FCC EAS search for grantee '{Grantee}' returned an empty body (no header row); skipping.",
                granteeName);
            return FccAuthReadResult.Failure(FccAuthOutcome.Malformed, "empty CSV (no header row)");
        }

        var header = ParseCsvLine(lines[0]);
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            // First occurrence of a column name wins; a duplicate header is ignored.
            columns.TryAdd(header[i], i);
        }

        // A response missing the columns the evidence needs is a bad/changed export, not a quiet grantee:
        // report Malformed so the collector does not treat it as silently "succeeded".
        if (!columns.TryGetValue(GranteeCodeColumn, out var granteeCodeIndex)
            || !columns.TryGetValue(ProductCodeColumn, out var productCodeIndex)
            || !columns.TryGetValue(GrantDateColumn, out var grantDateIndex)
            || !columns.TryGetValue(EquipmentClassColumn, out var equipmentClassIndex))
        {
            _logger.LogWarning(
                "FCC EAS search for grantee '{Grantee}' returned CSV without the expected columns "
                    + "(Grantee Code, Product Code, Grant Date, Equipment Class); skipping.",
                granteeName);
            return FccAuthReadResult.Failure(FccAuthOutcome.Malformed, "missing expected CSV columns");
        }

        var grants = new List<EquipmentAuthorization>();
        var truncated = false;
        for (var i = 1; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var fields = ParseCsvLine(lines[i]);
            // A short row that cannot supply every needed column is skipped rather than throwing.
            if (fields.Count <= grantDateIndex
                || fields.Count <= granteeCodeIndex
                || fields.Count <= productCodeIndex
                || fields.Count <= equipmentClassIndex)
            {
                continue;
            }

            // An unparseable/absent grant date is SKIPPED (not coerced to a min-value date): coercing would
            // inflate the grant count and silently mask export-shape drift in the Grant Date column.
            var grantDate = ParseGrantDate(fields[grantDateIndex]);
            if (grantDate is null)
            {
                continue;
            }

            var fccId = (fields[granteeCodeIndex] + fields[productCodeIndex]).Trim();
            if (string.IsNullOrWhiteSpace(fccId))
            {
                continue;
            }

            // The page cap is measured against VALID grants only (invalid/short/undated rows never counted),
            // so a further valid grant beyond the cap means the count is a floor, not a real total. Record that
            // and stop, rather than silently reporting exactly MaxPageSize as if it were complete.
            if (grants.Count >= _options.MaxPageSize)
            {
                truncated = true;
                break;
            }

            grants.Add(new EquipmentAuthorization(fccId, fields[equipmentClassIndex], grantDate.Value));
        }

        return FccAuthReadResult.Success(new FccAuthResult(grants.Count, grants, truncated));
    }

    private string BuildRequestUrl(string granteeName, DateOnly grantFloor)
    {
        var from = grantFloor.ToString(DateFormat, CultureInfo.InvariantCulture);
        // grant_date_to is "today" (UTC) from the injected TimeProvider — the recent window's upper bound.
        var to = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime).ToString(DateFormat, CultureInfo.InvariantCulture);

        return $"{BaseUrl}?{ModeParam}={ModeValue}&{CalledFromFrameParam}={CalledFromFrameValue}"
            + $"&{ApplicantNameParam}={Uri.EscapeDataString(granteeName)}"
            + $"&{GrantDateFromParam}={Uri.EscapeDataString(from)}"
            + $"&{GrantDateToParam}={Uri.EscapeDataString(to)}";
    }

    private static DateOnly? ParseGrantDate(string value) =>
        DateOnly.TryParseExact(
            value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Splits one CSV line into fields, honouring RFC-4180 double-quoted fields: a quoted field may contain
    /// commas (applicant names / equipment classes do), and a doubled quote (<c>""</c>) inside a quoted field
    /// is an escaped literal quote. Leading/trailing whitespace around an UNquoted field is trimmed.
    /// </summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
            }
            else
            {
                field.Append(ch);
            }
        }

        fields.Add(field.ToString().Trim());
        return fields;
    }
}
