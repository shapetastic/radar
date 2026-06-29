using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Radar.Application.Evidence;

/// <summary>
/// Deterministic, culture-invariant evidence normalizer. Cleans raw source text and
/// computes a stable SHA-256 content hash over the canonical title+body string.
/// Pure function: no I/O, no clock, no randomness.
/// </summary>
public sealed partial class EvidenceNormalizer : IEvidenceNormalizer
{
    public NormalizedEvidence Normalize(string? title, string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var normalizedText = NormalizeBody(rawText);
        var normalizedTitle = CollapseInlineWhitespace(CleanHtml(title ?? string.Empty).Trim());

        var canonical = normalizedTitle + "\n" + normalizedText;
        var contentHash = ComputeHash(canonical);

        return new NormalizedEvidence(normalizedText, contentHash);
    }

    /// <summary>
    /// Strips HTML/XML markup and decodes HTML entities, producing plain text.
    /// </summary>
    /// <remarks>
    /// Ordering is intentional: tags are stripped <b>before</b> entities are decoded.
    /// Decoding first would turn source-escaped literal text such as <c>&amp;lt;script&amp;gt;</c>
    /// into <c>&lt;script&gt;</c>, which the tag strip would then wrongly delete. Stripping first
    /// leaves escaped angle brackets to be decoded into literal <c>&lt;</c>/<c>&gt;</c> text,
    /// preserving them as content and keeping the transform faithful to the source's intent.
    /// </remarks>
    private static string CleanHtml(string raw)
    {
        // Drop <script>/<style> blocks entirely (tag + inner text); their bodies are never
        // human-readable evidence.
        var stripped = ScriptStyleBlockRegex().Replace(raw, " ");

        // Replace any remaining tag with a single space so block boundaries
        // (e.g. "...sentence.</p><p>Next...") do not word-join.
        stripped = TagRegex().Replace(stripped, " ");

        // Decode entities only after tags are gone (see remarks above).
        var decoded = WebUtility.HtmlDecode(stripped);

        // &nbsp; decodes to a non-breaking space (U+00A0); fold it to a regular space so the
        // existing inline-whitespace collapse can absorb it like any other run of spaces.
        return decoded.Replace(' ', ' ');
    }

    private static string NormalizeBody(string rawText)
    {
        // 0. Strip HTML markup and decode entities before any whitespace work.
        var cleaned = CleanHtml(rawText);

        // 1. Normalize line endings (\r\n and \r -> \n).
        var text = cleaned.Replace("\r\n", "\n").Replace('\r', '\n');

        var rawLines = text.Split('\n');

        // 2. Trim trailing whitespace from each line.
        // 4. Collapse runs of spaces/tabs within a line to a single space.
        var lines = new string[rawLines.Length];
        for (var i = 0; i < rawLines.Length; i++)
        {
            lines[i] = CollapseInlineWhitespace(rawLines[i].TrimEnd());
        }

        // 3. Collapse runs of three or more blank lines down to a single blank line.
        var builder = new StringBuilder(text.Length);
        var blankRun = 0;
        var first = true;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blankRun++;
                continue;
            }

            if (!first)
            {
                // Emit the preceding blank run: 1 or 2 blank lines pass through;
                // 3 or more collapse to a single blank line.
                var blanksToEmit = blankRun >= 3 ? 1 : blankRun;
                for (var b = 0; b < blanksToEmit; b++)
                {
                    builder.Append('\n');
                }

                builder.Append('\n');
            }

            builder.Append(line);
            blankRun = 0;
            first = false;
        }

        // 5. Trim the overall result (leading/trailing whitespace).
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Collapses runs of spaces and tabs within a single line to a single space.
    /// Assumes the input contains no line-ending characters.
    /// </summary>
    private static string CollapseInlineWhitespace(string line)
    {
        if (line.Length == 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var inWhitespace = false;
        foreach (var c in line)
        {
            if (c == ' ' || c == '\t')
            {
                if (!inWhitespace)
                {
                    builder.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                builder.Append(c);
                inWhitespace = false;
            }
        }

        return builder.ToString();
    }

    private static string ComputeHash(string canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleBlockRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();
}
