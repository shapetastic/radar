using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Radar.Infrastructure.Sec;

/// <summary>A candidate document row from an SEC filing-index table.</summary>
/// <param name="FileName">The linked <c>.htm</c>/<c>.html</c> document filename.</param>
/// <param name="Ex99Type">
/// The resolved EX-99 Type — the first cell of the row whose text is exactly <c>EX-99</c> / <c>EX-99.n</c> — or
/// null when the row is not an EX-99 exhibit. This is the ONLY type the earnings selector reads, and its
/// resolution is unchanged since spec 217 (spec 228 renamed it from <c>Type</c> so it cannot be mistaken for
/// <see cref="DocumentType"/>).
/// </param>
/// <param name="DocumentType">
/// SPEC 228 — the text of the row's <b>Type column</b> as the index table's header row names it (<c>8-K</c>,
/// <c>EX-10.1</c>, <c>EX-99.1</c>, …), resolved for EVERY row; null when the table carries no <c>Type</c> header
/// or the cell is blank. Read from the Type column only, never from the Description cell (which also often says
/// "8-K" or "EX-99.1" but is free text).
/// </param>
/// <param name="Size">The row's Size cell in bytes, for the largest-exhibit tie-break (0 when absent).</param>
/// <param name="Order">The row's position in document order — the deterministic tie-break (AD-3).</param>
/// <param name="LinkedThroughInlineViewer">
/// SPEC 228 — true when the document was linked through EDGAR's inline-XBRL viewer
/// (<c>/ix?doc=/Archives/…/{file}.htm</c>), which is how EDGAR links an iXBRL PRIMARY document.
/// </param>
internal sealed record SecFilingIndexRow(
    string FileName,
    string? Ex99Type,
    string? DocumentType,
    long Size,
    int Order,
    bool LinkedThroughInlineViewer);

/// <summary>
/// SPEC 228 — whether <see cref="SecFilingIndexTable.Parse"/> extracts documents linked through EDGAR's inline-XBRL
/// viewer.
/// </summary>
internal enum InlineViewerLinks
{
    /// <summary>
    /// Extract them: an <c>/ix?doc=/Archives/…/{file}.htm</c> href yields <c>{file}.htm</c>. The acquisition read
    /// uses this — without it the iXBRL primary 8-K document is invisible.
    /// </summary>
    Resolve = 1,

    /// <summary>
    /// Skip them exactly as every reader did before spec 228 (the href cut at <c>?</c> is <c>/ix</c>, not a
    /// document, and the row's next anchor is tried). The EARNINGS read uses this so its behaviour stays
    /// byte-identical (spec 228 §1): it never selects a primary document, and resolving one would only relabel
    /// an index whose sole <c>.htm</c> is an iXBRL primary from <c>Malformed</c> to <c>NoEarningsExhibit</c>.
    /// </summary>
    IgnoreAsBeforeSpec228 = 2,
}

/// <summary>
/// THE parser of an SEC EDGAR filing-index page's document table, extracted from
/// <see cref="HttpSecEarningsReleaseReader"/> (spec 217, reuse over copy — CLAUDE.md) so the earnings read
/// (EX-99.1 for item 2.02) and the acquisition read (the primary document plus EX-99.1 for item 1.01)
/// consume ONE definition. Two copies would drift: only one would get the next fix to the row/cell regexes
/// or the size parsing, and a drifted copy would make the two readers disagree about which documents a
/// filing has.
/// <para>
/// <b>Spec 228.</b> Since SEC's inline-XBRL rules EDGAR links the primary 8-K document through the inline viewer
/// (<c>href="/ix?doc=/Archives/edgar/data/…/myrg-20260908.htm"</c>). The pre-228 parser cut that href at <c>?</c>,
/// kept <c>/ix</c>, and dropped the row — so the acquisition read never saw the primary document, failed
/// outright on 31 of 185 live item-1.01 filings, and silently took an EX-10.1 / EX-2.1 / EX-1.1 / EX-4.1 exhibit
/// as "the primary" on 153 of the other 154. The viewer link is now resolved from its <c>doc</c> parameter
/// (<see cref="InlineViewerLinks.Resolve"/>), and every row's Type column is read (<see cref="SecFilingIndexRow.DocumentType"/>).
/// </para>
/// <para>
/// BCL regex only (no HTML-parser package, per spec 38). Pure: no HTTP, no clock, no state.
/// </para>
/// </summary>
internal static partial class SecFilingIndexTable
{
    /// <summary>
    /// The form types whose row IS the filing's primary document for the item-1.01 read (spec 228 §1 authority
    /// 2). Compared case-insensitively against <see cref="SecFilingIndexRow.DocumentType"/>.
    /// </summary>
    internal static readonly IReadOnlyList<string> PrimaryFormTypes = ["8-K", "8-K/A"];

    /// <summary>
    /// Parses the filing-index page's document table into candidate rows: the first linked
    /// <c>.htm</c>/<c>.html</c> Document filename, the resolved EX-99 Type (null when the row is not an
    /// EX-99 exhibit), the Type-column text (spec 228), and the Size (in bytes) for the fallback tie-break. Rows
    /// without a document filename (header rows, XBRL/graphic rows, the complete-submission <c>.txt</c>) are
    /// skipped. An empty list means no usable table was found.
    /// </summary>
    /// <param name="html">The index page.</param>
    /// <param name="inlineViewerLinks">Whether an inline-XBRL viewer link counts as a document (see <see cref="InlineViewerLinks"/>).</param>
    public static List<SecFilingIndexRow> Parse(
        string html, InlineViewerLinks inlineViewerLinks = InlineViewerLinks.Resolve)
    {
        var rows = new List<SecFilingIndexRow>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return rows;
        }

        // The Type column's index, from the most recent header row. Each EDGAR table ("Document Format Files",
        // "Data Files") carries its own header row, so the latest header governs the rows beneath it.
        int? typeColumn = null;

        var order = 0;
        foreach (Match rowMatch in TableRowRegex().Matches(html))
        {
            var rowHtml = rowMatch.Groups[1].Value;

            // A header row re-points the Type column. It is NOT skipped here: whether a row is a candidate stays
            // decided by its document link alone, exactly as before spec 228 (a header row links no document).
            var headerCells = HeaderCellRegex().Matches(rowHtml);
            if (headerCells.Count > 0)
            {
                typeColumn = null;
                for (var i = 0; i < headerCells.Count; i++)
                {
                    if (string.Equals(
                            CellText(headerCells[i].Groups[1].Value), TypeHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        typeColumn = i;
                        break;
                    }
                }
            }

            var (fileName, viaViewer) = ExtractDocumentFileName(rowHtml, inlineViewerLinks);
            if (fileName is null)
            {
                continue;
            }

            string? ex99Type = null;
            string? documentType = null;
            long size = 0;
            var cellIndex = 0;
            foreach (Match cellMatch in CellRegex().Matches(rowHtml))
            {
                var index = cellIndex++;
                var cellText = CellText(cellMatch.Groups[1].Value);
                if (cellText.Length == 0)
                {
                    continue;
                }

                if (index == typeColumn)
                {
                    documentType = cellText.ToUpperInvariant();
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

            rows.Add(new SecFilingIndexRow(fileName, ex99Type, documentType, size, order++, viaViewer));
        }

        return rows;
    }

    /// <summary>
    /// Selects the earnings-release exhibit: (1) the row whose EX-99 Type is exactly <c>EX-99.1</c>; (2) else any
    /// <c>EX-99.*</c> row — the largest by Size, then first in document order; (3) else null (no earnings
    /// exhibit). The primary 8-K is never a candidate (it carries no EX-99 Type). Unchanged by spec 228: it reads
    /// <see cref="SecFilingIndexRow.Ex99Type"/> only, never <see cref="SecFilingIndexRow.DocumentType"/>.
    /// </summary>
    public static SecFilingIndexRow? SelectEarningsExhibit(List<SecFilingIndexRow> rows)
    {
        var ex99 = rows.Where(r => r.Ex99Type is not null).ToList();
        if (ex99.Count == 0)
        {
            return null;
        }

        var exact = ex99
            .Where(r => string.Equals(r.Ex99Type, "EX-99.1", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Order)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        return ex99.OrderByDescending(r => r.Size).ThenBy(r => r.Order).First();
    }

    /// <summary>
    /// SPEC 217, rewritten by SPEC 228 — selects the filing's PRIMARY document (the 8-K body, where the item-1.01
    /// narrative lives) from what SEC says, never by position. Order of authority:
    /// <list type="number">
    /// <item>the file name the evidence metadata declares (the collector recorded it from SEC's own submissions
    /// feed), when the index actually carries that row;</item>
    /// <item>else the ONE row whose Type column is the form itself (<see cref="PrimaryFormTypes"/>).</item>
    /// </list>
    /// Anything else is a NAMED failure (<see cref="PrimaryDocumentSelection.FailureDetail"/>) — no form-typed row,
    /// or more than one — which the reader returns as a counted read failure.
    /// <para>
    /// (⚠ AMENDED in place by spec 228: this method used to fall back to "the first row whose Type is null", and
    /// its summary said it "never guesses". It did guess: <c>Type</c> was resolved only for EX-99 exhibits, and
    /// the parser dropped the iXBRL primary's row, so the first untyped row was an EX-10.1 credit agreement, an
    /// EX-2.1 merger agreement, an EX-1.1 underwriting agreement or an EX-4.1 indenture on 153 of 154 live reads.
    /// That fallback is deleted.)
    /// </para>
    /// </summary>
    public static PrimaryDocumentSelection SelectPrimaryDocument(
        List<SecFilingIndexRow> rows, string? declaredPrimaryDocument)
    {
        if (!string.IsNullOrWhiteSpace(declaredPrimaryDocument))
        {
            var declared = rows.FirstOrDefault(r => string.Equals(
                r.FileName, declaredPrimaryDocument.Trim(), StringComparison.OrdinalIgnoreCase));
            if (declared is not null)
            {
                return new PrimaryDocumentSelection(declared, PrimaryDocumentAuthority.Declared, FailureDetail: null);
            }
        }

        var formTyped = rows
            .Where(r => r.DocumentType is not null
                && PrimaryFormTypes.Contains(r.DocumentType, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return formTyped.Count switch
        {
            1 => new PrimaryDocumentSelection(formTyped[0], PrimaryDocumentAuthority.FormTyped, FailureDetail: null),
            0 => new PrimaryDocumentSelection(null, Authority: null, NoPrimaryDocumentRow),
            _ => new PrimaryDocumentSelection(
                null,
                Authority: null,
                $"{AmbiguousPrimaryDocumentRows} ({formTyped.Count.ToString(CultureInfo.InvariantCulture)} rows typed "
                    + $"{string.Join(" / ", PrimaryFormTypes)})"),
        };
    }

    /// <summary>The Type of the merger / acquisition agreement exhibit the item-1.01 read appends (spec 228 §1).</summary>
    internal const string MergerAgreementExhibitType = "EX-2.1";

    /// <summary>
    /// SPEC 228 §1 — the EX-2.1 (plan of acquisition / merger agreement) exhibit the acquisition read appends after
    /// EX-99.1: the first row, in document order, whose Type column is exactly <see cref="MergerAgreementExhibitType"/>;
    /// null when the index carries none. Only EX-2.1 — never an EX-10.* material contract (see
    /// <see cref="HttpSecAcquisitionFilingReader"/> for the measured decision).
    /// </summary>
    public static SecFilingIndexRow? SelectMergerAgreementExhibit(List<SecFilingIndexRow> rows) =>
        rows
            .Where(r => string.Equals(r.DocumentType, MergerAgreementExhibitType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Order)
            .FirstOrDefault();

    /// <summary>The named failure when neither authority yields a row (spec 228).</summary>
    internal const string NoPrimaryDocumentRow = "no primary document row (no declared primary in the index and no row typed 8-K or 8-K/A)";

    /// <summary>The named failure when more than one row is typed as the form (spec 228) — never resolved by position.</summary>
    internal const string AmbiguousPrimaryDocumentRows = "ambiguous primary document rows";

    /// <summary>
    /// Returns the first linked <c>.htm</c>/<c>.html</c> document filename in a table row (and whether it was
    /// linked through the inline-XBRL viewer), or a null filename when the row links no such document (header
    /// rows, XBRL/graphic rows).
    /// </summary>
    private static (string? FileName, bool ViaInlineViewer) ExtractDocumentFileName(
        string rowHtml, InlineViewerLinks inlineViewerLinks)
    {
        foreach (Match anchor in AnchorHrefRegex().Matches(rowHtml))
        {
            var href = anchor.Groups[1].Value;

            if (inlineViewerLinks == InlineViewerLinks.Resolve
                && TryInlineViewerDocument(href, out var viewerDocument))
            {
                var viewerFile = LastPathSegment(viewerDocument);
                if (IsHtmlDocument(viewerFile))
                {
                    return (viewerFile, true);
                }

                continue;
            }

            var fileName = LastPathSegment(href);
            if (IsHtmlDocument(fileName))
            {
                return (fileName, false);
            }
        }

        return (null, false);
    }

    private static bool IsHtmlDocument(string fileName) =>
        fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SPEC 228 — when <paramref name="href"/> is an EDGAR inline-XBRL viewer link (path <c>/ix</c>, relative or
    /// absolute), returns its <c>doc</c> parameter's value (entity- and percent-decoded).
    /// </summary>
    private static bool TryInlineViewerDocument(string href, out string document)
    {
        document = string.Empty;
        var decoded = WebUtility.HtmlDecode(href).Trim();
        var q = decoded.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return false;
        }

        var path = decoded[..q];
        if (!(path.Equals("/ix", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/ix", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var query = decoded[(q + 1)..];
        var hash = query.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            query = query[..hash];
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("doc=", StringComparison.OrdinalIgnoreCase))
            {
                document = Uri.UnescapeDataString(pair[4..]);
                return document.Length > 0;
            }
        }

        return false;
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

    /// <summary>The header text of the index table's Type column (spec 228).</summary>
    private const string TypeHeader = "Type";

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<th\b[^>]*>(.*?)</th>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeaderCellRegex();

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

/// <summary>Which authority chose a primary document (spec 228 §1).</summary>
internal enum PrimaryDocumentAuthority
{
    /// <summary>The evidence metadata's declared <c>primaryDocument</c>, carried by the index.</summary>
    Declared = 1,

    /// <summary>The one index row whose Type column is the form itself (<c>8-K</c> / <c>8-K/A</c>).</summary>
    FormTyped = 2,
}

/// <summary>
/// The answer of <see cref="SecFilingIndexTable.SelectPrimaryDocument"/>: the row and the authority that chose
/// it, or a null row with a NAMED <see cref="FailureDetail"/>.
/// </summary>
internal sealed record PrimaryDocumentSelection(
    SecFilingIndexRow? Row,
    PrimaryDocumentAuthority? Authority,
    string? FailureDetail);
