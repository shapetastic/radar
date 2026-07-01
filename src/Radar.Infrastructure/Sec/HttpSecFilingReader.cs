using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Fetches and parses a company's SEC EDGAR submissions JSON
/// (<c>https://data.sec.gov/submissions/CIK##########.json</c>) over HTTP using
/// <c>System.Text.Json</c>. A delisted, quiet, or unreachable issuer never crashes the run: non-success
/// status, transport errors, the request's own timeout, and malformed/absent JSON are each reported as a
/// typed failure on the returned <see cref="SecFilingReadResult"/> (with a warning) rather than swallowed;
/// caller-requested cancellation still throws. A 403 is called out distinctly because SEC returns it when
/// the mandatory <c>User-Agent</c> is missing/invalid. All HTTP/JSON/SEC code stays in Infrastructure (AD-5).
/// </summary>
internal sealed class HttpSecFilingReader : ISecFilingReader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpSecFilingReader> _logger;

    public HttpSecFilingReader(HttpClient httpClient, ILogger<HttpSecFilingReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SecFilingReadResult> ReadAsync(string submissionsUrl, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            using var response = await _httpClient
                .GetAsync(submissionsUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 403)
            {
                _logger.LogWarning(
                    "SEC submissions {SubmissionsUrl} returned HTTP 403 Forbidden; this is almost always a "
                        + "missing or invalid User-Agent (SEC requires a compliant 'Radar Research <email>' UA). Skipping.",
                    submissionsUrl);
                return SecFilingReadResult.Failure(SecFilingReadOutcome.Forbidden, "HTTP 403 (User-Agent)");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SEC submissions {SubmissionsUrl} returned non-success status {StatusCode}; skipping.",
                    submissionsUrl,
                    (int)response.StatusCode);
                return SecFilingReadResult.Failure(
                    SecFilingReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}");
            }

            // Materialize the body before disposing the response so parsing can happen synchronously.
            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SEC submissions {SubmissionsUrl} fetch failed; skipping.", submissionsUrl);
            return SecFilingReadResult.Failure(SecFilingReadOutcome.Unreachable, "transport error");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; do not hide it as a failure result.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
            _logger.LogWarning(ex, "SEC submissions {SubmissionsUrl} fetch timed out; skipping.", submissionsUrl);
            return SecFilingReadResult.Failure(SecFilingReadOutcome.Timeout, "request timed out");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var items = ParseFilings(document.RootElement, ct);
            return SecFilingReadResult.Success(items);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SEC submissions {SubmissionsUrl} returned malformed JSON; skipping.", submissionsUrl);
            return SecFilingReadResult.Failure(SecFilingReadOutcome.Malformed, "malformed JSON");
        }
    }

    /// <summary>
    /// Flattens the columnar <c>filings.recent</c> parallel arrays (newest-first) into
    /// <see cref="SecFilingItem"/>s. Rows missing a form, accession, or a parseable acceptance instant are
    /// skipped rather than throwing. An absent <c>filings.recent</c> shape yields no items (a quiet issuer),
    /// not a malformed error.
    /// </summary>
    private static IReadOnlyList<SecFilingItem> ParseFilings(JsonElement root, CancellationToken ct)
    {
        var items = new List<SecFilingItem>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return items;
        }

        var cik = StripLeadingZeros(GetString(root, "cik"));

        if (!root.TryGetProperty("filings", out var filings)
            || filings.ValueKind != JsonValueKind.Object
            || !filings.TryGetProperty("recent", out var recent)
            || recent.ValueKind != JsonValueKind.Object)
        {
            return items;
        }

        var form = GetArray(recent, "form");
        var filingDate = GetArray(recent, "filingDate");
        var reportDate = GetArray(recent, "reportDate");
        var acceptance = GetArray(recent, "acceptanceDateTime");
        var accession = GetArray(recent, "accessionNumber");
        var primaryDocument = GetArray(recent, "primaryDocument");
        var primaryDocDescription = GetArray(recent, "primaryDocDescription");
        var itemCodes = GetArray(recent, "items");

        var count = form.Count;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var formValue = At(form, i);
            var accessionValue = At(accession, i);
            if (string.IsNullOrWhiteSpace(formValue) || string.IsNullOrWhiteSpace(accessionValue))
            {
                continue;
            }

            if (!TryParseAcceptance(At(acceptance, i), out var acceptanceUtc))
            {
                continue;
            }

            items.Add(new SecFilingItem(
                Form: formValue,
                FilingDate: At(filingDate, i) ?? string.Empty,
                ReportDate: NullIfBlank(At(reportDate, i)),
                AcceptanceDateTimeUtc: acceptanceUtc,
                Accession: accessionValue,
                PrimaryDocument: NullIfBlank(At(primaryDocument, i)),
                PrimaryDocDescription: NullIfBlank(At(primaryDocDescription, i)),
                Items: NullIfBlank(At(itemCodes, i)),
                IndexUrl: BuildIndexUrl(cik, accessionValue)));
        }

        return items;
    }

    /// <summary>
    /// Builds the stable filing index landing page URL:
    /// <c>https://www.sec.gov/Archives/edgar/data/{cik}/{accNoNoDashes}/{accessionWithDashes}-index.htm</c>.
    /// </summary>
    private static string BuildIndexUrl(string cik, string accession)
    {
        var accNoNoDashes = accession.Replace("-", string.Empty, StringComparison.Ordinal);
        return $"https://www.sec.gov/Archives/edgar/data/{cik}/{accNoNoDashes}/{accession}-index.htm";
    }

    private static bool TryParseAcceptance(string? value, out DateTimeOffset utc)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            utc = parsed.ToUniversalTime();
            return true;
        }

        utc = default;
        return false;
    }

    private static IReadOnlyList<JsonElement> GetArray(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            return array.EnumerateArray().ToList();
        }

        return [];
    }

    private static string? At(IReadOnlyList<JsonElement> array, int index)
    {
        if (index < 0 || index >= array.Count)
        {
            return null;
        }

        var element = array[index];
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string StripLeadingZeros(string cik)
    {
        var trimmed = cik.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
