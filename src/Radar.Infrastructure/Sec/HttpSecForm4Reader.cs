using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using Radar.Domain.Signals;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Fetches a company's SEC EDGAR submissions JSON, filters it to Form 4 filings, then for each Form 4
/// fetches and parses the raw structured ownership XML document, classifies its insider transactions by SEC
/// transaction code, and produces a filing-level insider-activity <see cref="SignalDirection"/> +
/// net dollar value. The XML parse is deterministic (rule-based, NO AI). A delisted/quiet/unreachable issuer
/// never crashes the run: a submissions-level non-success/transport/timeout/malformed condition is reported
/// as a typed failure on <see cref="SecForm4ReadResult"/> (with a warning) and degrades the whole feed; a
/// single bad Form 4 (missing/non-XML primary document, per-filing fetch failure, or malformed XML) is
/// skipped (logged) while the other filings in the same feed still count. Caller-requested cancellation
/// still throws. A 403 is called out distinctly because SEC returns it when the mandatory <c>User-Agent</c>
/// is missing/invalid. All HTTP/JSON/XML/SEC code stays in Infrastructure (AD-5), reusing
/// <see cref="SecEdgarUrls"/> and <see cref="SecHttpFetch"/> — no duplicated SEC URL/HTTP logic.
/// </summary>
internal sealed class HttpSecForm4Reader : ISecForm4Reader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpSecForm4Reader> _logger;
    private readonly int _maxFilingsPerCompany;

    public HttpSecForm4Reader(
        HttpClient httpClient,
        ILogger<HttpSecForm4Reader> logger,
        SecForm4CollectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _logger = logger;
        _maxFilingsPerCompany = options.MaxFilingsPerCompany;
    }

    public async Task<SecForm4ReadResult> ReadAsync(string submissionsUrl, CancellationToken ct)
    {
        // Step 1 — fetch the company's submissions JSON, reusing the shared SEC GET + outcome-mapping ladder.
        var (failure, bytes) = await SecHttpFetch.GetAsync<SecForm4ReadResult, byte[]>(
            _httpClient,
            submissionsUrl,
            readBody: (content, c) => content.ReadAsByteArrayAsync(c),
            onForbidden: () =>
            {
                _logger.LogWarning(
                    "SEC Form 4 submissions {SubmissionsUrl} returned HTTP 403 Forbidden; this is almost always a "
                        + "missing or invalid User-Agent (SEC requires a compliant 'Radar Research <email>' UA). Skipping.",
                    submissionsUrl);
                return SecForm4ReadResult.Failure(SecForm4ReadOutcome.Forbidden, "HTTP 403 (User-Agent)");
            },
            onHttpError: status =>
            {
                _logger.LogWarning(
                    "SEC Form 4 submissions {SubmissionsUrl} returned non-success status {StatusCode}; skipping.",
                    submissionsUrl,
                    status);
                return SecForm4ReadResult.Failure(SecForm4ReadOutcome.HttpError, $"HTTP {status}");
            },
            onUnreachable: ex =>
            {
                _logger.LogWarning(ex, "SEC Form 4 submissions {SubmissionsUrl} fetch failed; skipping.", submissionsUrl);
                return SecForm4ReadResult.Failure(SecForm4ReadOutcome.Unreachable, "transport error");
            },
            onTimeout: ex =>
            {
                _logger.LogWarning(ex, "SEC Form 4 submissions {SubmissionsUrl} fetch timed out; skipping.", submissionsUrl);
                return SecForm4ReadResult.Failure(SecForm4ReadOutcome.Timeout, "request timed out");
            },
            ct).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        string cik;
        IReadOnlyList<SecRecentFilingRow> rows;
        try
        {
            // Non-null once we are past the failure guard above: SecHttpFetch only defaults the body on failure.
            using var document = JsonDocument.Parse(bytes!);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "SEC Form 4 submissions {SubmissionsUrl} returned JSON with an unexpected root kind {RootKind} "
                        + "(expected an object); skipping.",
                    submissionsUrl,
                    document.RootElement.ValueKind);
                return SecForm4ReadResult.Failure(
                    SecForm4ReadOutcome.Malformed, "unexpected root JSON shape");
            }

            // A blank cik or an absent filings.recent object is a malformed/changed payload, NOT a quiet
            // issuer: proceeding would derive archive URLs from a CIK of "0" and report Success with zero
            // items, hiding the feed breakage. Report it as a typed Malformed failure instead (mirrors the
            // unexpected-root-shape guard above and HttpSecFilingReader).
            cik = GetString(document.RootElement, "cik");
            if (string.IsNullOrWhiteSpace(cik))
            {
                _logger.LogWarning(
                    "SEC Form 4 submissions {SubmissionsUrl} JSON is missing or blank 'cik'; treating as malformed and skipping.",
                    submissionsUrl);
                return SecForm4ReadResult.Failure(SecForm4ReadOutcome.Malformed, "missing cik");
            }

            if (!TryGetRecent(document.RootElement, out var recent))
            {
                _logger.LogWarning(
                    "SEC Form 4 submissions {SubmissionsUrl} JSON lacks the expected 'filings.recent' object; "
                        + "treating as malformed and skipping.",
                    submissionsUrl);
                return SecForm4ReadResult.Failure(SecForm4ReadOutcome.Malformed, "missing filings.recent");
            }

            // Reuse the shared columnar filings.recent flattener; Form 4's per-source hook is simply the
            // form == "4" predicate (the subsequent ownership-XML fetch + classify stays below).
            rows = SecRecentFilings.Flatten(
                recent,
                form => string.Equals(form, "4", StringComparison.Ordinal),
                _maxFilingsPerCompany,
                ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SEC Form 4 submissions {SubmissionsUrl} returned malformed JSON; skipping.", submissionsUrl);
            return SecForm4ReadResult.Failure(SecForm4ReadOutcome.Malformed, "malformed JSON");
        }

        // Step 2/3 — per Form 4 row: derive the raw ownership-XML URL, fetch it, parse + classify. A single
        // bad filing is skipped (logged), never fatal — only the submissions read above degrades the feed.
        var filings = new List<SecForm4Filing>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var filing = await ReadOneFilingAsync(cik, row, ct).ConfigureAwait(false);
            if (filing is not null)
            {
                filings.Add(filing);
            }
        }

        return SecForm4ReadResult.Success(filings);
    }

    private async Task<SecForm4Filing?> ReadOneFilingAsync(string cik, SecRecentFilingRow row, CancellationToken ct)
    {
        // Step 2 — derive the raw ownership-XML file name from primaryDocument (strip any leading
        // xslF345XNN/ path segment); a non-.xml primary document is a typed no-op (not a throw).
        var rawFile = StripToRawXmlFile(row.PrimaryDocument);
        if (rawFile is null)
        {
            _logger.LogDebug(
                "SEC Form 4 {Accession} primaryDocument '{PrimaryDocument}' is not an ownership XML document; skipping filing.",
                row.Accession,
                row.PrimaryDocument);
            return null;
        }

        var xmlUrl = $"{SecEdgarUrls.BuildArchiveBaseUrl(cik, row.Accession)}/{rawFile}";

        // A per-filing fetch failure skips this ONE filing (warning), never the whole feed.
        var (failed, xml) = await SecHttpFetch.GetAsync<object, string>(
            _httpClient,
            xmlUrl,
            readBody: (content, c) => content.ReadAsStringAsync(c),
            onForbidden: () => Skip(),
            onHttpError: _ => Skip(),
            onUnreachable: _ => Skip(),
            onTimeout: _ => Skip(),
            ct).ConfigureAwait(false);

        if (failed is not null || xml is null)
        {
            _logger.LogWarning(
                "SEC Form 4 ownership XML {XmlUrl} (accession {Accession}) could not be fetched; skipping filing.",
                xmlUrl,
                row.Accession);
            return null;
        }

        // Step 3 — parse the ownership XML (root <ownershipDocument>, no namespace) and classify.
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(
                ex,
                "SEC Form 4 ownership XML {XmlUrl} (accession {Accession}) was malformed; skipping filing.",
                xmlUrl,
                row.Accession);
            return null;
        }

        var root = doc.Root;
        if (root is null)
        {
            return null;
        }

        return Classify(cik, row, root);
    }

    // Shared sentinel for a per-filing skip: SecHttpFetch requires a non-null TFailure on every failure path.
    private static object Skip() => new();

    /// <summary>
    /// Derives the raw ownership-XML file name from <c>primaryDocument</c>: if it contains a <c>/</c>, take
    /// the last path segment (this strips the leading <c>xslF345XNN/</c> XSL-render folder); otherwise use it
    /// as-is. Returns <c>null</c> when the result does not end <c>.xml</c> (case-insensitive) — a Form 4 whose
    /// primary document is not an XML ownership document is skipped upstream.
    /// </summary>
    private static string? StripToRawXmlFile(string? primaryDocument)
    {
        if (string.IsNullOrWhiteSpace(primaryDocument))
        {
            return null;
        }

        var trimmed = primaryDocument.Trim();
        var slash = trimmed.LastIndexOf('/');
        var rawFile = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;

        if (!rawFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return rawFile;
    }

    /// <summary>
    /// Builds the filing-level <see cref="SecForm4Filing"/> from the parsed ownership XML: reads the 10b5-1
    /// plan flag, the issuer ticker, the distinct reporting-owner set, and each non-derivative/derivative
    /// transaction; classifies every transaction by code; and resolves a single filing-level direction +
    /// net value per the deterministic rules (a 10b5-1 plan forces every transaction Neutral).
    /// </summary>
    private static SecForm4Filing Classify(string cik, SecRecentFilingRow row, XElement root)
    {
        var is10b5Plan = ReadIs10b5Plan(root);

        var issuerTicker = NullIfBlank((string?)root
            .Elements("issuer")
            .Elements("issuerTradingSymbol")
            .FirstOrDefault());

        // Distinct reporting owners (by name, case-insensitive-trim); first owner name is used in the phrase.
        var ownerNames = root
            .Elements("reportingOwner")
            .Elements("reportingOwnerId")
            .Elements("rptOwnerName")
            .Select(e => (e.Value ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .ToList();

        var primaryOwnerName = ownerNames.Count > 0 ? ownerNames[0] : string.Empty;
        var distinctOwnerCount = ownerNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Transactions only (skip <nonDerivativeHolding>/<derivativeHolding> — no coding/amounts).
        var transactions = root
            .Elements("nonDerivativeTable").Elements("nonDerivativeTransaction")
            .Concat(root.Elements("derivativeTable").Elements("derivativeTransaction"));

        decimal buyValue = 0m;
        decimal sellValue = 0m;
        decimal buyShares = 0m;
        decimal sellShares = 0m;

        foreach (var txn in transactions)
        {
            // A 10b5-1 plan forces every transaction Neutral: a planned sale is not a discretionary signal.
            if (is10b5Plan)
            {
                continue;
            }

            var code = (string?)txn.Elements("transactionCoding").Elements("transactionCode").FirstOrDefault();
            var classification = SecForm4TransactionCode.Classify(code);
            if (classification == InsiderTxnClassification.NeutralExcluded)
            {
                continue;
            }

            var amounts = txn.Element("transactionAmounts");
            var shares = ReadDecimal(amounts?.Elements("transactionShares").Elements("value").FirstOrDefault());
            var price = ReadDecimal(amounts?.Elements("transactionPricePerShare").Elements("value").FirstOrDefault());
            var value = shares * price;

            if (classification == InsiderTxnClassification.Buy)
            {
                buyValue += value;
                buyShares += shares;
            }
            else
            {
                sellValue += value;
                sellShares += shares;
            }
        }

        // Filing-level direction from the discretionary buy/sell dollar aggregates (grants price 0 add 0).
        SignalDirection direction;
        decimal netValue;
        decimal shareCount;
        if (buyValue > 0m && sellValue == 0m)
        {
            direction = SignalDirection.Positive;
            netValue = buyValue;
            shareCount = buyShares;
        }
        else if (sellValue > 0m && buyValue == 0m)
        {
            direction = SignalDirection.Negative;
            netValue = sellValue;
            shareCount = sellShares;
        }
        else if (buyValue > 0m && sellValue > 0m)
        {
            // A mixed same-filing buy+sell is genuinely ambiguous — Neutral, do NOT net-sign it.
            direction = SignalDirection.Neutral;
            netValue = Math.Max(buyValue, sellValue);
            shareCount = 0m;
        }
        else
        {
            direction = SignalDirection.Neutral;
            netValue = 0m;
            shareCount = 0m;
        }

        // Within-filing cluster: >= 2 distinct reporting owners transacting in a resolved direction.
        var hasCluster = distinctOwnerCount >= 2
            && (direction == SignalDirection.Positive || direction == SignalDirection.Negative);

        return new SecForm4Filing(
            Accession: row.Accession,
            FilingDate: row.FilingDate,
            AcceptanceDateTimeUtc: row.AcceptanceDateTimeUtc,
            IndexUrl: SecEdgarUrls.BuildIndexUrl(cik, row.Accession, ".htm"),
            IssuerTicker: issuerTicker,
            PrimaryOwnerName: primaryOwnerName,
            DistinctOwnerCount: distinctOwnerCount,
            Direction: direction,
            NetValue: netValue,
            Shares: shareCount,
            HasCluster: hasCluster,
            Is10b5Plan: is10b5Plan);
    }

    /// <summary>
    /// Reads the document-level 10b5-1 pre-arranged-plan flag: the <c>&lt;aff10b5One&gt;</c> element carries a
    /// boolean string in both forms (<c>true</c>/<c>false</c> and <c>1</c>/<c>0</c>) — <c>true</c>/<c>1</c>
    /// (case-insensitive, trimmed) is a plan; <c>false</c>/<c>0</c>/empty/absent is not. As a belt-and-braces
    /// secondary indicator, a <c>&lt;footnote&gt;</c> whose text contains <c>10b5-1</c> is also treated as a plan.
    /// </summary>
    private static bool ReadIs10b5Plan(XElement root)
    {
        foreach (var flag in root.Descendants("aff10b5One"))
        {
            var value = (flag.Value ?? string.Empty).Trim();
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1")
            {
                return true;
            }
        }

        foreach (var footnote in root.Descendants("footnote"))
        {
            if ((footnote.Value ?? string.Empty).Contains("10b5-1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static decimal ReadDecimal(XElement? element)
    {
        if (element is null)
        {
            return 0m;
        }

        return decimal.TryParse(
            element.Value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;
    }

    /// <summary>
    /// Resolves the <c>filings.recent</c> object (both must be present and objects). Returns <c>false</c> when
    /// the expected submissions shape is absent — the caller treats that as a typed <c>Malformed</c> failure
    /// rather than a quiet zero-item success.
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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
