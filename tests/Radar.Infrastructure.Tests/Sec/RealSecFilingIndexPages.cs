namespace Radar.Infrastructure.Tests.Sec;

/// <summary>
/// SPEC 228 — four REAL SEC EDGAR filing-index pages, trimmed (each file's leading comment states its source URL,
/// fetch date and what was removed). Every one links its primary 8-K document ONLY through the inline-XBRL viewer
/// (<c>/ix?doc=…</c>), which the pre-228 parser could not see.
/// </summary>
internal static class RealSecFilingIndexPages
{
    /// <summary>MYRG 0000700923-26-000047 — iXBRL primary, no <c>.htm</c> exhibit (pre-228: "no parseable document table").</summary>
    public static string MyrgNoExhibits => Load("myrg-0000700923-26-000047-index.html");

    /// <summary>HZO 0001193125-26-290439 — iXBRL primary + EX-99.1 only (pre-228: "no primary document row").</summary>
    public static string HzoEx991Only => Load("hzo-0001193125-26-290439-index.html");

    /// <summary>SHOO 0001641172-25-008949 — iXBRL primary + EX-10.1 + EX-99.1 (pre-228: the EX-10.1 was taken as primary).</summary>
    public static string ShooCreditAgreementAndResults => Load("shoo-0001641172-25-008949-index.html");

    /// <summary>
    /// HZO 0001193125-26-341302 — iXBRL primary, then EX-2.1 (merger agreement), then EX-99.1 in document order
    /// (pre-228: the merger agreement was taken as primary; since spec 228 it is appended after EX-99.1).
    /// </summary>
    public static string HzoMergerAgreement => Load("hzo-0001193125-26-341302-index.html");

    private static string Load(string fileName)
    {
        var name = "Radar.Infrastructure.Tests.Sec.Fixtures." + fileName;
        using var stream = typeof(RealSecFilingIndexPages).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
