using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Radar.Infrastructure.News;

/// <summary>
/// THE deterministic HTML → visible plain text rendering (spec 177): remove <c>script</c>/<c>style</c>/
/// <c>nav</c> element CONTENT, strip every remaining tag, HTML-decode entities, and collapse all whitespace
/// runs to single spaces. One shared helper, used by BOTH the RSS <c>&lt;description&gt;</c> rendering
/// (<see cref="HttpNewsSearchReader"/>) and the safe publisher-content extractor
/// (<see cref="HttpNewsArticleContentReader"/>) — a second copy would silently drift and two "identical"
/// payloads would stop hashing identically.
/// <para>
/// Pure and culture-invariant: regex-based, no clock, no I/O, no randomness (AD-3). It is deliberately a
/// crude visible-text pass, not an HTML parser — determinism and honesty ("this is what the markup's text
/// content said") beat fidelity here, and the RAW payload is always preserved alongside it.
/// </para>
/// </summary>
internal static partial class HtmlVisibleText
{
    /// <summary>
    /// The extractor's versioned identity, recorded on every content-fetch result and folded into the
    /// retrieval-policy identity. Changing ANY rule below (the removed elements, the strip order, the
    /// whitespace collapse) is a new observation/content version: bump this — never edit in place silently.
    /// </summary>
    public const string Version = "news-text-v1";

    [GeneratedRegex(@"<(script|style|nav)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RemovedElements();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    /// <summary>
    /// Renders markup to collapsed visible text. Returns the empty string for null/blank input or markup
    /// with no visible text (callers decide whether empty means <c>null</c> or a typed outcome).
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        // Order matters and is part of the versioned contract: element CONTENT first (a <script> body may
        // contain '<' that is not a tag), then comments, then remaining tags, then entity decoding — so an
        // encoded "&lt;script&gt;" in TEXT is decoded to literal text rather than re-interpreted as markup.
        var text = RemovedElements().Replace(html, " ");
        text = Comments().Replace(text, " ");
        text = Tags().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return CollapseWhitespace(text);
    }

    /// <summary>
    /// <see cref="ToPlainText"/> with the declared character cap the article extractor is bounded by. The
    /// cap applies to the RENDERED text (UTF-16 code units), never splits a surrogate pair, and truncation
    /// is explicit — a prefix is never passed off as complete content.
    /// </summary>
    public static string Extract(string? html, int maxChars, out bool truncated)
    {
        var text = ToPlainText(html);
        if (text.Length <= maxChars)
        {
            truncated = false;
            return text;
        }

        truncated = true;
        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }

    /// <summary>Collapses every whitespace run to a single space and trims (the shared collector rule shape).</summary>
    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
