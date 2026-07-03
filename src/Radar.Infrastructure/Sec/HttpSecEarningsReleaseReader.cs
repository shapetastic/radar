using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Radar.Application.Evidence;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// Fetches an SEC EDGAR filing's <c>{accession}-index.html</c> page over HTTP, parses its document table
/// with BCL regex (no HTML-parser package, per spec 38), selects the earnings-release exhibit — the row
/// whose Type is exactly <c>EX-99.1</c>, else an <c>EX-99.*</c> fallback (largest by Size, then document
/// order), never the boilerplate primary 8-K — fetches that exhibit, and returns its body as plain text
/// produced by the shared <see cref="IEvidenceNormalizer"/> stripper. A quiet or unreachable filing never
/// crashes the run: non-success status, transport errors, the request's own timeout, a missing exhibit,
/// and an unparseable index are each reported as a typed failure on the returned
/// <see cref="SecEarningsReleaseReadResult"/> (with a warning) rather than swallowed; caller-requested
/// cancellation still throws. A 403 is called out distinctly because SEC returns it when the mandatory
/// <c>User-Agent</c> is missing/invalid. All HTTP/HTML/SEC code stays in Infrastructure (AD-5).
/// </summary>
internal sealed partial class HttpSecEarningsReleaseReader : ISecEarningsReleaseReader
{
    private readonly HttpClient _httpClient;
    private readonly IEvidenceNormalizer _normalizer;
    private readonly ILogger<HttpSecEarningsReleaseReader> _logger;

    public HttpSecEarningsReleaseReader(
        HttpClient httpClient,
        IEvidenceNormalizer normalizer,
        ILogger<HttpSecEarningsReleaseReader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _normalizer = normalizer;
        _logger = logger;
    }

    public async Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cik);
        ArgumentException.ThrowIfNullOrWhiteSpace(accession);

        // Same URL facts the spec-56 reader computes: CIK with leading zeros stripped, accession with dashes
        // removed in the path (but the dashed accession is kept in the index filename).
        var cikNoZeros = StripLeadingZeros(cik.Trim());
        var dashedAccession = accession.Trim();
        var accNoNoDashes = dashedAccession.Replace("-", string.Empty, StringComparison.Ordinal);
        var baseUrl = $"https://www.sec.gov/Archives/edgar/data/{cikNoZeros}/{accNoNoDashes}";
        var indexUrl = $"{baseUrl}/{dashedAccession}-index.html";

        var (indexFailure, indexBody) = await FetchAsync(indexUrl, ct).ConfigureAwait(false);
        if (indexFailure is not null)
        {
            return indexFailure;
        }

        var rows = ParseDocumentTable(indexBody);
        if (rows.Count == 0)
        {
            _logger.LogWarning(
                "SEC filing index {IndexUrl} had no parseable document table; skipping earnings-release read.",
                indexUrl);
            return SecEarningsReleaseReadResult.Failure(
                SecEarningsReleaseReadOutcome.Malformed, "no parseable document table");
        }

        var selected = SelectEarningsExhibit(rows);
        if (selected is null)
        {
            // The index parsed fine but carries no EX-99.* exhibit; never fall back to the boilerplate 8-K.
            _logger.LogInformation(
                "SEC filing index {IndexUrl} carried no EX-99.* earnings-release exhibit; skipping.", indexUrl);
            return SecEarningsReleaseReadResult.Failure(
                SecEarningsReleaseReadOutcome.NoEarningsExhibit, "no EX-99.* exhibit row");
        }

        var exhibitUrl = $"{baseUrl}/{selected.FileName}";

        var (exhibitFailure, exhibitBody) = await FetchAsync(exhibitUrl, ct).ConfigureAwait(false);
        if (exhibitFailure is not null)
        {
            return exhibitFailure;
        }

        // Reuse the shared HTML stripper (spec 38) — tag-stripped, entity-decoded, whitespace-collapsed plain
        // text. The normalizer also computes a content hash we ignore here; reuse over a second stripper.
        var plainText = _normalizer.Normalize(title: null, rawText: exhibitBody).NormalizedText;

        // SelectEarningsExhibit only returns EX-99 rows, whose Type is guaranteed non-null.
        return SecEarningsReleaseReadResult.Success(plainText, selected.Type!, selected.FileName);
    }

    /// <summary>
    /// Fetches a URL as a string, mapping SEC's HTTP outcomes to typed failures exactly as
    /// <see cref="HttpSecFilingReader"/> does. Returns a non-null failure (and empty body) on any bad
    /// response; caller-requested cancellation re-throws.
    /// </summary>
    private async Task<(SecEarningsReleaseReadResult? Failure, string Body)> FetchAsync(
        string url, CancellationToken ct)
    {
        // Honour caller cancellation before each (sequential) request, independent of transport timing.
        ct.ThrowIfCancellationRequested();

        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 403)
            {
                _logger.LogWarning(
                    "SEC {Url} returned HTTP 403 Forbidden; this is almost always a missing or invalid "
                        + "User-Agent (SEC requires a compliant 'Radar Research <email>' UA). Skipping.",
                    url);
                return (
                    SecEarningsReleaseReadResult.Failure(
                        SecEarningsReleaseReadOutcome.Forbidden, "HTTP 403 (User-Agent)"),
                    string.Empty);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SEC {Url} returned non-success status {StatusCode}; skipping.",
                    url,
                    (int)response.StatusCode);
                return (
                    SecEarningsReleaseReadResult.Failure(
                        SecEarningsReleaseReadOutcome.HttpError, $"HTTP {(int)response.StatusCode}"),
                    string.Empty);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (null, body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SEC {Url} fetch failed; skipping.", url);
            return (
                SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.Unreachable, "transport error"),
                string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation must propagate so the run stops; do not hide it as a failure result.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Non-ct cancellation here is an HTTP timeout (the request's own deadline); treat it as a skip.
            _logger.LogWarning(ex, "SEC {Url} fetch timed out; skipping.", url);
            return (
                SecEarningsReleaseReadResult.Failure(SecEarningsReleaseReadOutcome.Timeout, "request timed out"),
                string.Empty);
        }
    }

    /// <summary>
    /// Parses the filing index's document table into candidate document rows. Each returned row carries the
    /// linked <c>.htm</c>/<c>.html</c> Document filename, the resolved EX-99 Type (null when the row is not an
    /// EX-99 exhibit), and the Size (in bytes) for the fallback tie-break. Rows without a document filename
    /// (header rows, XBRL/graphic rows) are skipped. An empty list means no usable table was found.
    /// </summary>
    private static List<DocumentRow> ParseDocumentTable(string html)
    {
        var rows = new List<DocumentRow>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return rows;
        }

        var order = 0;
        foreach (Match rowMatch in TableRowRegex().Matches(html))
        {
            var rowHtml = rowMatch.Groups[1].Value;

            var fileName = ExtractDocumentFileName(rowHtml);
            if (fileName is null)
            {
                continue;
            }

            string? ex99Type = null;
            long size = 0;
            foreach (Match cellMatch in CellRegex().Matches(rowHtml))
            {
                var cellText = CellText(cellMatch.Groups[1].Value);
                if (cellText.Length == 0)
                {
                    continue;
                }

                if (ex99Type is null && Ex99TypeRegex().IsMatch(cellText))
                {
                    ex99Type = cellText.ToUpperInvariant();
                }

                var cellSize = ParseSize(cellText);
                if (cellSize > size)
                {
                    size = cellSize;
                }
            }

            rows.Add(new DocumentRow(fileName, ex99Type, size, order++));
        }

        return rows;
    }

    /// <summary>
    /// Selects the earnings-release exhibit: (1) the row whose Type is exactly <c>EX-99.1</c>; (2) else any
    /// <c>EX-99.*</c> row — the largest by Size, then first in document order; (3) else null (no earnings
    /// exhibit). The primary 8-K is never a candidate (it carries no EX-99 Type).
    /// </summary>
    private static DocumentRow? SelectEarningsExhibit(List<DocumentRow> rows)
    {
        var ex99 = rows.Where(r => r.Type is not null).ToList();
        if (ex99.Count == 0)
        {
            return null;
        }

        var exact = ex99
            .Where(r => string.Equals(r.Type, "EX-99.1", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Order)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        return ex99.OrderByDescending(r => r.Size).ThenBy(r => r.Order).First();
    }

    /// <summary>
    /// Returns the first linked <c>.htm</c>/<c>.html</c> document filename in a table row, or null when the
    /// row links no such document (header rows, XBRL/graphic rows).
    /// </summary>
    private static string? ExtractDocumentFileName(string rowHtml)
    {
        foreach (Match anchor in AnchorHrefRegex().Matches(rowHtml))
        {
            var href = anchor.Groups[1].Value;
            var fileName = LastPathSegment(href);
            if (fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }
        }

        return null;
    }

    private static string LastPathSegment(string href)
    {
        // Drop any query/fragment, then take the segment after the last '/'.
        var cut = href.AsSpan();
        var q = cut.IndexOfAny('?', '#');
        if (q >= 0)
        {
            cut = cut[..q];
        }

        var slash = cut.LastIndexOf('/');
        return (slash >= 0 ? cut[(slash + 1)..] : cut).Trim().ToString();
    }

    /// <summary>Strips tags, decodes HTML entities, and collapses whitespace in a single table cell.</summary>
    private static string CellText(string cellHtml)
    {
        var noTags = TagRegex().Replace(cellHtml, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    /// <summary>
    /// Parses a Size-cell value (e.g. <c>"321 KB"</c>) into a byte count for the fallback tie-break. A cell
    /// without an explicit KB/MB/GB/bytes unit (e.g. the Seq column) returns 0 so it is never mistaken for a
    /// size.
    /// </summary>
    private static long ParseSize(string cellText)
    {
        var match = SizeRegex().Match(cellText);
        if (!match.Success)
        {
            return 0;
        }

        var digits = match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "KB" => value * 1024L,
            "MB" => value * 1024L * 1024L,
            "GB" => value * 1024L * 1024L * 1024L,
            _ => value,
        };
    }

    private static string StripLeadingZeros(string cik)
    {
        var trimmed = cik.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>A candidate document row from the filing index table.</summary>
    private sealed record DocumentRow(string FileName, string? Type, long Size, int Order);

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex("<a\\b[^>]*?\\bhref\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorHrefRegex();

    [GeneratedRegex("<[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^EX-99(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Ex99TypeRegex();

    [GeneratedRegex(@"^([\d,]+)\s*(KB|MB|GB|bytes?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
