namespace Radar.Infrastructure.Sources;

/// <summary>
/// Shared single-key <c>key=&lt;value&gt;</c> split for the feed-Url token parsers that carry exactly one
/// key (e.g. <c>assignee=&lt;name&gt;</c> for patents, <c>applicant=&lt;name&gt;</c> for FDA). The caller
/// passes the key INCLUDING its trailing <c>=</c> (e.g. <c>"assignee="</c>); the value is everything after
/// the FIRST occurrence of that key, trimmed. Tokens are NOT URL-decoded, so a value's own literal
/// spaces/commas stay intact. Consolidates the byte-identical single-key split previously inlined in
/// <c>PatentFeedTarget</c> (<c>assignee=…</c>), now also used by <c>FdaFeedTarget</c> (<c>applicant=…</c>);
/// the blank/null value policy stays an explicit per-caller hook in the parsers themselves (mirroring
/// <see cref="TwoKeyFeedToken"/>, whose per-caller blank-value policy is likewise the caller's).
/// </summary>
internal static class SingleKeyFeedToken
{
    /// <summary>
    /// Splits <paramref name="trimmedToken"/> (already trimmed by the caller) on the single
    /// <paramref name="key"/> (passed WITH its trailing <c>=</c>). Returns <see langword="false"/> — with
    /// <paramref name="value"/> empty — when the key is missing. Otherwise returns <see langword="true"/> with
    /// <paramref name="value"/> = the trimmed remainder after the key (which may be empty; the caller decides
    /// whether an empty value is acceptable).
    /// </summary>
    public static bool TrySplit(string trimmedToken, string key, out string value)
    {
        value = string.Empty;

        var keyIndex = trimmedToken.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return false;
        }

        value = trimmedToken[(keyIndex + key.Length)..].Trim();
        return true;
    }
}
