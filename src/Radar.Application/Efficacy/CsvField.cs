namespace Radar.Application.Efficacy;

/// <summary>
/// The ONE minimal CSV field-escaping rule shared by every efficacy export (reuse over copy — CLAUDE.md).
/// Extracted from <see cref="EfficacyCsvRenderer"/> when the strategy-comparison leaderboard needed the same
/// rule; both call sites route through it, so a future fix cannot land on only one copy.
/// <para>
/// The rule: an empty/null value renders as an empty cell; a value containing none of <c>,</c> <c>"</c>
/// <c>\n</c> <c>\r</c> renders verbatim; anything else is wrapped in double quotes with embedded quotes
/// doubled. Pure and culture-free (AD-3): identical input yields byte-identical output.
/// </para>
/// </summary>
internal static class CsvField
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
