namespace Radar.Infrastructure.Sources;

/// <summary>
/// Shared single-key split for the feed-Url token parsers that carry ONE <c>key=&lt;value&gt;</c> pair
/// (e.g. patents' <c>assignee=…</c>, FCC's <c>grantee=…</c>). The single-key analogue of
/// <see cref="TwoKeyFeedToken"/>: the key may appear anywhere in the (already-trimmed) token and owns
/// everything after it to the end — so the value's own literal spaces and commas stay intact (tokens are NOT
/// URL-decoded; our seeds never put a second key after a single-key value). The value is trimmed; whether a
/// blank value is acceptable is the CALLER's policy (an explicit per-caller hook, exactly as
/// <see cref="TwoKeyFeedToken"/> frames it — <c>PatentFeedTarget</c> and <c>FccFeedTarget</c> both reject a
/// blank value). Consolidates the byte-identical single-key split previously inlined in
/// <c>PatentFeedTarget</c> (spec 127); now also used by <c>FccFeedTarget</c> (spec 128). Keep per-parser
/// semantics (blank-value policy) in the parsers themselves, not here.
/// </summary>
internal static class SingleKeyFeedToken
{
    /// <summary>
    /// Splits <paramref name="trimmedToken"/> (already trimmed by the caller) on the single
    /// <paramref name="key"/>. Returns <see langword="false"/> — with an empty out value — when the key is
    /// absent. On success <paramref name="value"/> is everything after the key, trimmed; it may be empty (the
    /// caller decides whether an empty value is acceptable).
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
