namespace Radar.Infrastructure.Sec;

/// <summary>
/// Single owner of SEC EDGAR URL construction and CIK canonicalisation for the SEC readers. Consolidates
/// the byte-identical StripLeadingZeros + Archives/edgar/data path building previously copied into
/// <see cref="HttpSecFilingReader"/> (the reference) and <see cref="HttpSecEarningsReleaseReader"/>. Pure
/// string logic, no HTTP.
/// </summary>
internal static class SecEdgarUrls
{
    /// <summary>
    /// Canonical CIK for a URL path: leading zeros stripped; an all-zero or empty CIK collapses to "0".
    /// (Callers that receive a raw CIK trim it first — this method does not trim surrounding whitespace.)
    /// </summary>
    public static string StripLeadingZeros(string cik)
    {
        var trimmed = cik.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>
    /// The archive base for a filing:
    /// <c>https://www.sec.gov/Archives/edgar/data/{cikNoZeros}/{accNoNoDashes}</c>, where the CIK has leading
    /// zeros stripped and the accession has its dashes removed.
    /// </summary>
    public static string BuildArchiveBaseUrl(string cik, string accession)
    {
        var cikNoZeros = StripLeadingZeros(cik);
        var accNoNoDashes = accession.Replace("-", string.Empty, StringComparison.Ordinal);
        return $"https://www.sec.gov/Archives/edgar/data/{cikNoZeros}/{accNoNoDashes}";
    }

    /// <summary>
    /// The filing index landing page: <c>{BuildArchiveBaseUrl}/{accession}-index{extension}</c>. The DASHED
    /// accession is kept in the filename; the de-dashed accession is only the archive path segment.
    /// <see cref="HttpSecFilingReader"/> surfaces the <c>.htm</c> form on <c>SecFilingItem.IndexUrl</c>;
    /// <see cref="HttpSecEarningsReleaseReader"/> fetches the <c>.html</c> form. Both extensions are preserved
    /// exactly — do not unify them.
    /// </summary>
    public static string BuildIndexUrl(string cik, string accession, string extension) =>
        $"{BuildArchiveBaseUrl(cik, accession)}/{accession}-index{extension}";
}
