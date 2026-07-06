using System.Globalization;
using System.Text.Json;

namespace Radar.Infrastructure.Sec;

/// <summary>
/// A single flattened row from a company's columnar <c>filings.recent</c> submissions block, before any
/// per-source (Form 4 ownership-XML / 13D-13G form-classify) work. Carries only the columns the SEC readers
/// share: the dashed accession, the filing date, the parsed UTC acceptance instant, the primary document, and
/// the raw form string (so a caller can classify it).
/// </summary>
internal sealed record SecRecentFilingRow(
    string Accession,
    string FilingDate,
    DateTimeOffset AcceptanceDateTimeUtc,
    string? PrimaryDocument,
    string Form);

/// <summary>
/// Shared columnar <c>filings.recent</c> flattener for the SEC submissions readers. SEC lists a company's
/// recent filings as parallel arrays (<c>form</c> / <c>filingDate</c> / <c>acceptanceDateTime</c> /
/// <c>accessionNumber</c> / <c>primaryDocument</c>) rather than an array of objects; this reassembles them
/// into aligned <see cref="SecRecentFilingRow"/> records, keeping only rows whose form matches the caller's
/// predicate (the per-source hook), that carry a non-blank accession, and whose acceptance instant parses
/// (culture-invariant, coerced to UTC). Rows are already newest-first in the source arrays, so the first
/// <paramref name="maxRows"/> matches are the most recent. Consolidates the flattening previously copied into
/// <see cref="HttpSecForm4Reader"/> so <see cref="HttpSec13DGReader"/> reuses it rather than pasting a second
/// copy (reuse-over-copy). Pure parsing, no HTTP; the divergent per-source behaviour (Form 4's ownership-XML
/// fetch, 13D/13G's form-classify) stays in each reader.
/// <para>
/// This type is also the single home for the low-level submissions-JSON guards/primitives shared by the three
/// SEC submissions readers (<see cref="HttpSecFilingReader"/>, <see cref="HttpSecForm4Reader"/>,
/// <see cref="HttpSec13DGReader"/>): <see cref="TryGetRecent"/> (the <c>filings.recent</c> shape guard),
/// <see cref="GetArray"/>, <see cref="At"/>, <see cref="GetString"/>, <see cref="TryParseAcceptance"/>, and
/// <see cref="NullIfBlank"/>. They were previously byte-identical copies scattered across those readers;
/// consolidating them here keeps a single source of truth (reuse-over-copy) so a fix cannot drift across copies.
/// </para>
/// </summary>
internal static class SecRecentFilings
{
    /// <summary>
    /// Flattens <paramref name="recent"/> (the <c>filings.recent</c> object) into aligned rows, keeping only
    /// rows where <paramref name="formPredicate"/> accepts the row's form string, capped at
    /// <paramref name="maxRows"/> (newest-first). Rows with a blank accession or an unparseable acceptance
    /// instant are skipped.
    /// </summary>
    public static IReadOnlyList<SecRecentFilingRow> Flatten(
        JsonElement recent,
        Func<string, bool> formPredicate,
        int maxRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(formPredicate);

        var rows = new List<SecRecentFilingRow>();

        var form = GetArray(recent, "form");
        var filingDate = GetArray(recent, "filingDate");
        var acceptance = GetArray(recent, "acceptanceDateTime");
        var accession = GetArray(recent, "accessionNumber");
        var primaryDocument = GetArray(recent, "primaryDocument");

        var count = form.Count;
        for (var i = 0; i < count && rows.Count < maxRows; i++)
        {
            ct.ThrowIfCancellationRequested();

            var formValue = At(form, i);
            if (string.IsNullOrWhiteSpace(formValue) || !formPredicate(formValue))
            {
                continue;
            }

            var accessionValue = At(accession, i);
            if (string.IsNullOrWhiteSpace(accessionValue))
            {
                continue;
            }

            if (!TryParseAcceptance(At(acceptance, i), out var acceptanceUtc))
            {
                continue;
            }

            rows.Add(new SecRecentFilingRow(
                Accession: accessionValue,
                FilingDate: At(filingDate, i) ?? string.Empty,
                AcceptanceDateTimeUtc: acceptanceUtc,
                PrimaryDocument: NullIfBlank(At(primaryDocument, i)),
                Form: formValue));
        }

        return rows;
    }

    /// <summary>
    /// Resolves the <c>filings.recent</c> object (both <c>filings</c> and <c>recent</c> must be present and
    /// objects). Returns <c>false</c> (with <paramref name="recent"/> = <c>default</c>) when the expected
    /// submissions shape is absent — a caller may treat that as a typed <c>Malformed</c> failure rather than a
    /// quiet zero-item success.
    /// </summary>
    internal static bool TryGetRecent(JsonElement root, out JsonElement recent)
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

    internal static bool TryParseAcceptance(string? value, out DateTimeOffset utc)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            utc = parsed.ToUniversalTime();
            return true;
        }

        utc = default;
        return false;
    }

    internal static IReadOnlyList<JsonElement> GetArray(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            return array.EnumerateArray().ToList();
        }

        return [];
    }

    internal static string? At(IReadOnlyList<JsonElement> array, int index)
    {
        if (index < 0 || index >= array.Count)
        {
            return null;
        }

        var element = array[index];
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>
    /// Reads a top-level string property, returning <see cref="string.Empty"/> when it is absent or not a JSON
    /// string. Non-nullable empty fallback — distinct from <see cref="At"/>, which returns <c>string?</c>.
    /// </summary>
    internal static string GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    internal static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
