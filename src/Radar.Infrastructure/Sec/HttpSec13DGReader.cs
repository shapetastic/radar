using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Fetches a company's SEC EDGAR submissions JSON, filters it to Schedule 13D/13G beneficial-ownership
/// filings, and classifies each by form type (deterministic, rule-based, NO AI). v1 is metadata-only: unlike
/// <see cref="HttpSecForm4Reader"/> there is NO per-filing body fetch — the free-form 13D/13G filing body has
/// no reliable structured XML, so direction/strength come from the form string alone (spec 99's fixed rules).
/// A delisted/quiet/unreachable issuer never crashes the run: a submissions-level non-success/transport/
/// timeout/malformed condition is reported as a typed failure on <see cref="Sec13DGReadResult"/> (with a
/// warning) and degrades the whole feed. Caller-requested cancellation still throws. A 403 is called out
/// distinctly because SEC returns it when the mandatory <c>User-Agent</c> is missing/invalid. All HTTP/JSON/SEC
/// code stays in Infrastructure (AD-5), reusing <see cref="SecEdgarUrls"/>, <see cref="SecHttpFetch"/>, and the
/// shared columnar <see cref="SecRecentFilings"/> flattener — no duplicated SEC URL/HTTP/columnar logic.
/// </summary>
internal sealed class HttpSec13DGReader : ISec13DGReader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpSec13DGReader> _logger;
    private readonly int _maxFilingsPerCompany;

    public HttpSec13DGReader(
        HttpClient httpClient,
        ILogger<HttpSec13DGReader> logger,
        Sec13DGCollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _logger = logger;
        _maxFilingsPerCompany = options.MaxFilingsPerCompany;
    }

    public async Task<Sec13DGReadResult> ReadAsync(string submissionsUrl, CancellationToken ct)
    {
        // Fetch the company's submissions JSON, reusing the shared SEC GET + outcome-mapping ladder.
        var (failure, bytes) = await SecHttpFetch.GetAsync<Sec13DGReadResult, byte[]>(
            _httpClient,
            submissionsUrl,
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onForbidden: () =>
            {
                _logger.LogWarning(
                    "SEC 13D/13G submissions {SubmissionsUrl} returned HTTP 403 Forbidden; this is almost always a "
                        + "missing or invalid User-Agent (SEC requires a compliant 'Radar Research <email>' UA). Skipping.",
                    submissionsUrl);
                return Sec13DGReadResult.Failure(Sec13DGReadOutcome.Forbidden, "HTTP 403 (User-Agent)");
            },
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "SEC 13D/13G submissions {SubmissionsUrl} returned non-success status {StatusCode}; skipping.",
                    submissionsUrl,
                    status);
                return Sec13DGReadResult.Failure(Sec13DGReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(ex, "SEC 13D/13G submissions {SubmissionsUrl} fetch failed; skipping.", submissionsUrl);
                return Sec13DGReadResult.Failure(Sec13DGReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                _logger.LogWarning(ex, "SEC 13D/13G submissions {SubmissionsUrl} fetch timed out; skipping.", submissionsUrl);
                return Sec13DGReadResult.Failure(Sec13DGReadOutcome.Timeout, "request timed out");
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

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "SEC 13D/13G submissions {SubmissionsUrl} returned JSON with an unexpected root kind {RootKind} "
                        + "(expected an object); skipping.",
                    submissionsUrl,
                    document.RootElement.ValueKind);
                return Sec13DGReadResult.Failure(Sec13DGReadOutcome.Malformed, "unexpected root JSON shape");
            }

            // A blank cik or an absent filings.recent object is a malformed/changed payload, NOT a quiet
            // issuer: proceeding would derive index URLs from a CIK of "0" and report Success with zero items,
            // hiding the feed breakage. Report it as a typed Malformed failure instead (mirrors HttpSecForm4Reader).
            var cik = GetString(document.RootElement, "cik");
            if (string.IsNullOrWhiteSpace(cik))
            {
                _logger.LogWarning(
                    "SEC 13D/13G submissions {SubmissionsUrl} JSON is missing or blank 'cik'; treating as malformed and skipping.",
                    submissionsUrl);
                return Sec13DGReadResult.Failure(Sec13DGReadOutcome.Malformed, "missing cik");
            }

            if (!TryGetRecent(document.RootElement, out var recent))
            {
                _logger.LogWarning(
                    "SEC 13D/13G submissions {SubmissionsUrl} JSON lacks the expected 'filings.recent' object; "
                        + "treating as malformed and skipping.",
                    submissionsUrl);
                return Sec13DGReadResult.Failure(Sec13DGReadOutcome.Malformed, "missing filings.recent");
            }

            // Flatten via the shared columnar helper; the per-source hook is the 13D/13G form predicate.
            var rows = SecRecentFilings.Flatten(
                recent,
                Sec13DGFormType.IsBeneficialOwnershipForm,
                _maxFilingsPerCompany,
                ct);

            var filings = new List<Sec13DGFiling>(rows.Count);
            foreach (var row in rows)
            {
                var category = Sec13DGFormType.Classify(row.Form);
                if (category == Sec13DGCategory.NotApplicable)
                {
                    // Defensive — the predicate already excluded these.
                    continue;
                }

                filings.Add(new Sec13DGFiling(
                    Accession: row.Accession,
                    FilingDate: row.FilingDate,
                    AcceptanceDateTimeUtc: row.AcceptanceDateTimeUtc,
                    IndexUrl: SecEdgarUrls.BuildIndexUrl(cik, row.Accession, ".htm"),
                    Form: row.Form,
                    Category: category));
            }

            return Sec13DGReadResult.Success(filings);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SEC 13D/13G submissions {SubmissionsUrl} returned malformed JSON; skipping.", submissionsUrl);
            return Sec13DGReadResult.Failure(Sec13DGReadOutcome.Malformed, "malformed JSON");
        }
    }

    /// <summary>
    /// Resolves the <c>filings.recent</c> object (both <c>filings</c> and <c>recent</c> must be present and
    /// objects). Returns <c>false</c> when the expected submissions shape is absent — the caller treats that as
    /// a typed <c>Malformed</c> failure rather than a quiet zero-item success.
    /// </summary>
    private static bool TryGetRecent(JsonElement root, out JsonElement recent)
    {
        if (root.TryGetProperty("filings", out var filings)
            && filings.ValueKind == JsonValueKind.Object
            && filings.TryGetProperty("recent", out var r)
            && r.ValueKind == JsonValueKind.Object)
        {
            recent = r;
            return true;
        }

        recent = default;
        return false;
    }

    private static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
