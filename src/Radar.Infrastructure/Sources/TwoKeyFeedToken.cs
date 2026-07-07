namespace Radar.Infrastructure.Sources;

/// <summary>
/// Shared, order-robust two-key <c>&amp;</c>-split for the feed-Url token parsers
/// (<c>key1=&lt;value&gt;&amp;key2=&lt;value&gt;</c>). Whichever key appears first in the token owns
/// everything up to the FIRST <c>&amp;</c> after its value start (which must sit before the other key), and
/// the other key owns everything after — so a value's own literal spaces stay intact (tokens are NOT
/// URL-decoded; our seeds never put <c>&amp;</c> inside a value). Values are trimmed; blank values are the
/// CALLER's policy (e.g. <c>QueryFeedTarget</c> treats an empty ticker as "no ticker" while
/// <c>UsaSpendingFeedTarget</c>/<c>HiringFeedTarget</c> reject any blank value). Consolidates the
/// byte-identical split previously copied across <c>QueryFeedTarget</c> (<c>query=…&amp;ticker=…</c>) and
/// <c>UsaSpendingFeedTarget</c> (<c>recipientId=…&amp;recipientSearchText=…</c>), now also used by
/// <c>HiringFeedTarget</c> (<c>platform=…&amp;board=…</c>); per-parser semantics (optional keys, blank-value
/// policy) stay explicit per-caller hooks in the parsers themselves.
/// </summary>
internal static class TwoKeyFeedToken
{
    /// <summary>
    /// Splits <paramref name="trimmedToken"/> (already trimmed by the caller) on the two keys, robust to
    /// key ordering. Returns <see langword="false"/> — with empty out values — when either key is missing
    /// or the boundary <c>&amp;</c> between the two keys is absent/misplaced (a malformed token). Values
    /// come back trimmed and may be empty (the caller decides whether an empty value is acceptable).
    /// </summary>
    public static bool TrySplit(
        string trimmedToken, string keyA, string keyB, out string valueA, out string valueB)
    {
        valueA = string.Empty;
        valueB = string.Empty;

        var aIndex = trimmedToken.IndexOf(keyA, StringComparison.Ordinal);
        var bIndex = trimmedToken.IndexOf(keyB, StringComparison.Ordinal);
        if (aIndex < 0 || bIndex < 0)
        {
            return false;
        }

        if (aIndex < bIndex)
        {
            // keyA=<value>&keyB=<value> — split on the FIRST '&' between the two keys.
            var aValueStart = aIndex + keyA.Length;
            var boundary = trimmedToken.IndexOf('&', aValueStart);
            if (boundary < 0 || boundary >= bIndex)
            {
                return false;
            }

            valueA = trimmedToken[aValueStart..boundary].Trim();
            valueB = trimmedToken[(bIndex + keyB.Length)..].Trim();
        }
        else
        {
            // keyB=<value>&keyA=<value>
            var bValueStart = bIndex + keyB.Length;
            var boundary = trimmedToken.IndexOf('&', bValueStart);
            if (boundary < 0 || boundary >= aIndex)
            {
                return false;
            }

            valueB = trimmedToken[bValueStart..boundary].Trim();
            valueA = trimmedToken[(aIndex + keyA.Length)..].Trim();
        }

        return true;
    }
}
