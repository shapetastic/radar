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
        var (failure, bytes) = await SecHttpFetch.GetAsync<SecFilingReadResult, byte[]>(
            _httpClient,
            submissionsUrl,
            // Materialize the body before disposing the response so parsing can happen synchronously.
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onForbidden: () =>
            {
                _logger.LogWarning(
                    "SEC submissions {SubmissionsUrl} returned HTTP 403 Forbidden; this is almost always a "
                        + "missing or invalid User-Agent (SEC requires a compliant 'Radar Research <email>' UA). Skipping.",
                    submissionsUrl);
                return SecFilingReadResult.Failure(SecFilingReadOutcome.Forbidden, "HTTP 403 (User-Agent)");
            },
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "SEC submissions {SubmissionsUrl} returned non-success status {StatusCode}; skipping.",
                    submissionsUrl,
                    status);
                return SecFilingReadResult.Failure(SecFilingReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(ex, "SEC submissions {SubmissionsUrl} fetch failed; skipping.", submissionsUrl);
                return SecFilingReadResult.Failure(SecFilingReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
                _logger.LogWarning(ex, "SEC submissions {SubmissionsUrl} fetch timed out; skipping.", submissionsUrl);
                return SecFilingReadResult.Failure(SecFilingReadOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        try
        {
            // Non-null once we are past the failure guard above: SecHttpFetch only defaults the body on failure.
            using var document = JsonDocument.Parse(bytes!);

            // The submissions endpoint always returns a JSON object. Valid JSON with any other root shape
            // (array, string, number, …) is a bad/changed response, not a quiet issuer: report it as
            // Malformed so the collector does not treat the source as silently "succeeded".
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "SEC submissions {SubmissionsUrl} returned JSON with an unexpected root kind {RootKind} "
                        + "(expected an object); skipping.",
                    submissionsUrl,
                    document.RootElement.ValueKind);
                return SecFilingReadResult.Failure(
                    SecFilingReadOutcome.Malformed, "unexpected root JSON shape");
            }

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

        var cik = SecRecentFilings.GetString(root, "cik");

        if (!SecRecentFilings.TryGetRecent(root, out var recent))
        {
            return items;
        }

        var form = SecRecentFilings.GetArray(recent, "form");
        var filingDate = SecRecentFilings.GetArray(recent, "filingDate");
        var reportDate = SecRecentFilings.GetArray(recent, "reportDate");
        var acceptance = SecRecentFilings.GetArray(recent, "acceptanceDateTime");
        var accession = SecRecentFilings.GetArray(recent, "accessionNumber");
        var primaryDocument = SecRecentFilings.GetArray(recent, "primaryDocument");
        var primaryDocDescription = SecRecentFilings.GetArray(recent, "primaryDocDescription");
        var itemCodes = SecRecentFilings.GetArray(recent, "items");

        var count = form.Count;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var formValue = SecRecentFilings.At(form, i);
            var accessionValue = SecRecentFilings.At(accession, i);
            if (string.IsNullOrWhiteSpace(formValue) || string.IsNullOrWhiteSpace(accessionValue))
            {
                continue;
            }

            if (!SecRecentFilings.TryParseAcceptance(SecRecentFilings.At(acceptance, i), out var acceptanceUtc))
            {
                continue;
            }

            items.Add(new SecFilingItem(
                Form: formValue,
                FilingDate: SecRecentFilings.At(filingDate, i) ?? string.Empty,
                ReportDate: SecRecentFilings.NullIfBlank(SecRecentFilings.At(reportDate, i)),
                AcceptanceDateTimeUtc: acceptanceUtc,
                Accession: accessionValue,
                PrimaryDocument: SecRecentFilings.NullIfBlank(SecRecentFilings.At(primaryDocument, i)),
                PrimaryDocDescription: SecRecentFilings.NullIfBlank(SecRecentFilings.At(primaryDocDescription, i)),
                Items: SecRecentFilings.NullIfBlank(SecRecentFilings.At(itemCodes, i)),
                IndexUrl: SecEdgarUrls.BuildIndexUrl(cik, accessionValue, ".htm")));
        }

        return items;
    }
}
