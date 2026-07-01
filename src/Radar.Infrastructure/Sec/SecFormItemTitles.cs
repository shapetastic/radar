namespace Radar.Infrastructure.Sec;

/// <summary>
/// A static, fixed lookup of material SEC Form 8-K item codes to their <b>official</b> item titles
/// (from the EDGAR 8-K item list). This encodes the event <i>type</i> only — an item code never reveals
/// the event's <i>direction</i> (e.g. <c>2.02</c> "Results of Operations" is a beat or a miss; the code
/// alone cannot say). The collector uses this to enrich filing evidence text with real, matchable
/// business phrases <b>alongside</b> the raw codes (provenance is preserved, nothing is fabricated).
/// Unmapped codes are intentionally left bare — no title is invented for them. SEC item-code knowledge
/// stays inside <c>Radar.Infrastructure/Sec</c> (AD-5).
/// </summary>
internal static class SecFormItemTitles
{
    private static readonly IReadOnlyDictionary<string, string> Titles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1.01"] = "Entry into a Material Definitive Agreement",
            ["2.01"] = "Completion of Acquisition or Disposition of Assets",
            ["2.02"] = "Results of Operations and Financial Condition",
            ["2.03"] = "Creation of a Direct Financial Obligation",
            ["3.02"] = "Unregistered Sales of Equity Securities",
            ["5.02"] = "Departure of Directors or Certain Officers; Election of Directors; Appointment of Certain Officers",
        };

    /// <summary>
    /// Resolves the comma-separated 8-K <paramref name="itemCodes"/> to their official item titles,
    /// preserving order and skipping any code that has no known title (no fabricated titles). Duplicate
    /// titles are emitted at most once. Returns an empty list when nothing resolves.
    /// </summary>
    public static IReadOnlyList<string> ResolveTitles(string? itemCodes)
    {
        if (string.IsNullOrWhiteSpace(itemCodes))
        {
            return [];
        }

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in itemCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Titles.TryGetValue(raw, out var title) && seen.Add(title))
            {
                resolved.Add(title);
            }
        }

        return resolved;
    }
}
