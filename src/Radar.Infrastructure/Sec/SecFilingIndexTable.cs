using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Radar.Infrastructure.Sec;

/// <summary>A candidate document row from an SEC filing-index table.</summary>
/// <param name="FileName">The linked <c>.htm</c>/<c>.html</c> document filename.</param>
/// <param name="Type">The resolved EX-99 Type, or null when the row is not an EX-99 exhibit.</param>
/// <param name="Size">The row's Size cell in bytes, for the largest-exhibit tie-break (0 when absent).</param>
/// <param name="Order">The row's position in document order — the deterministic tie-break (AD-3).</param>
internal sealed record SecFilingIndexRow(string FileName, string? Type, long Size, int Order);

/// <summary>
/// THE parser of an SEC EDGAR filing-index page's document table, extracted from
/// <see cref="HttpSecEarningsReleaseReader"/> (spec 217, reuse over copy — CLAUDE.md) so the earnings read
/// (EX-99.1 for item 2.02) and the acquisition read (the primary document plus EX-99.1 for item 1.01)
/// consume ONE definition. Two copies would drift: only one would get the next fix to the row/cell regexes
/// or the size parsing, and a drifted copy would make the two readers disagree about which documents a
/// filing has.
/// <para>
/// BCL regex only (no HTML-parser package, per spec 38). Pure: no HTTP, no clock, no state.
/// </para>
/// </summary>
internal static partial class SecFilingIndexTable
{
    /// <summary>
    /// Parses the filing-index page's document table into candidate rows: the first linked
    /// <c>.htm</c>/<c>.html</c> Document filename, the resolved EX-99 Type (null when the row is not an
    /// EX-99 exhibit), and the Size (in bytes) for the fallback tie-break. Rows without a document filename
    /// (header rows, XBRL/graphic rows) are skipped. An empty list means no usable table was found.
    /// </summary>
    public static List<SecFilingIndexRow> Parse(string html)
    {
        var rows = new List<SecFilingIndexRow>();
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

            rows.Add(new SecFilingIndexRow(fileName, ex99Type, size, order++));
        }

        return rows;
    }

    /// <summary>
    /// Selects the earnings-release exhibit: (1) the row whose Type is exactly <c>EX-99.1</c>; (2) else any
    /// <c>EX-99.*</c> row — the largest by Size, then first in document order; (3) else null (no earnings
    /// exhibit). The primary 8-K is never a candidate (it carries no EX-99 Type).
    /// </summary>
    public static SecFilingIndexRow? SelectEarningsExhibit(List<SecFilingIndexRow> rows)
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
    /// SPEC 217 — selects the filing's PRIMARY document (the 8-K body, where the item-1.01 narrative lives).
    /// Prefers the file name the evidence metadata declares, when the index actually carries that row: the
    /// collector recorded it from SEC's own submissions feed, so it is the authoritative answer. Otherwise
    /// falls back to the first NON-EX-99 row in document order (a row whose <c>Type</c> is null — <c>Type</c>
    /// is resolved only for EX-99.* exhibits), which is where SEC places the primary document. Returns null
    /// when the index carries no such row — never guesses.
    /// </summary>
    public static SecFilingIndexRow? SelectPrimaryDocument(
        List<SecFilingIndexRow> rows, string? declaredPrimaryDocument)
    {
        if (!string.IsNullOrWhiteSpace(declaredPrimaryDocument))
        {
            var declared = rows.FirstOrDefault(r => string.Equals(
                r.FileName, declaredPrimaryDocument.Trim(), StringComparison.OrdinalIgnoreCase));
            if (declared is not null)
            {
                return declared;
            }
        }

        return rows.Where(r => r.Type is null).OrderBy(r => r.Order).FirstOrDefault();
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
