using System.Text;

namespace Radar.CalibrationAudit;

/// <summary>
/// Minimal RFC-4180 CSV escape + line parser for the audit's own artifacts (worksheet, manifest,
/// exclusion lists). The shared Application <c>CsvField</c> escaper is <c>internal</c> to
/// <c>Radar.Application</c> (visible only to its own tests), so this research console — whose one
/// sanctioned internal-access grant is into <c>Radar.Infrastructure</c> — carries its own copy of the
/// same quoting rule (quote when the field contains a comma, quote, CR or LF; double embedded quotes).
/// </summary>
public static class Csv
{
    public static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        var mustQuote = field.AsSpan().IndexOfAny(',', '"') >= 0
            || field.AsSpan().IndexOfAny('\r', '\n') >= 0;
        if (!mustQuote)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string Line(params string?[] fields) =>
        string.Join(",", fields.Select(Escape));

    /// <summary>
    /// Parses one CSV line into fields (RFC-4180 quoting). Used only to re-read the audit's OWN manifest
    /// for re-runnability; it never parses third-party CSV.
    /// </summary>
    public static IReadOnlyList<string> ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
