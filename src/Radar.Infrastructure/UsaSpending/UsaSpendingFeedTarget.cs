namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// The two per-company inputs a <c>usaspending</c> feed carries in its single <c>Url</c> field, parsed
/// from the documented token <c>recipientId=&lt;recipient_id&gt;&amp;recipientSearchText=&lt;recipient name&gt;</c>
/// (e.g. <c>recipientId=af09eaba-71de-97b6-660d-1adac9349c4d-C&amp;recipientSearchText=Mercury Systems</c>).
/// This keeps the shared <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record unchanged.
/// <see cref="RecipientSearchText"/> is the fuzzy full-text query sent to the API; <see cref="RecipientId"/>
/// is the exact key the collector client-side-filters returned rows on (the search is fuzzy and can match
/// subsidiaries/unrelated entities). An unparsable token yields <see langword="null"/> so the collector can
/// degrade it to a source failure rather than throwing.
/// </summary>
internal sealed record UsaSpendingFeedTarget(string RecipientId, string RecipientSearchText)
{
    private const string RecipientIdKey = "recipientId=";
    private const string SearchTextKey = "recipientSearchText=";

    /// <summary>
    /// Parses a <c>recipientId=...&amp;recipientSearchText=...</c> token. Robust to key ordering and
    /// surrounding whitespace; the search text's literal spaces are preserved (the token is NOT
    /// URL-decoded). Returns <see langword="null"/> when either key is missing/empty or the token is
    /// blank/malformed.
    /// </summary>
    public static UsaSpendingFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();

        // Locate both keys, then split on the FIRST '&' that sits between the two so the recipient name's
        // own characters (which never contain '&' in our seeds) stay intact. Whichever key comes first
        // owns everything up to that boundary; the other owns everything after.
        var idKeyIndex = trimmed.IndexOf(RecipientIdKey, StringComparison.Ordinal);
        var searchKeyIndex = trimmed.IndexOf(SearchTextKey, StringComparison.Ordinal);
        if (idKeyIndex < 0 || searchKeyIndex < 0)
        {
            return null;
        }

        string recipientId;
        string searchText;

        if (idKeyIndex < searchKeyIndex)
        {
            // recipientId=<id>&recipientSearchText=<name>
            var idValueStart = idKeyIndex + RecipientIdKey.Length;
            var boundary = trimmed.IndexOf('&', idValueStart);
            if (boundary < 0 || boundary >= searchKeyIndex)
            {
                return null;
            }

            recipientId = trimmed[idValueStart..boundary].Trim();
            searchText = trimmed[(searchKeyIndex + SearchTextKey.Length)..].Trim();
        }
        else
        {
            // recipientSearchText=<name>&recipientId=<id>
            var searchValueStart = searchKeyIndex + SearchTextKey.Length;
            var boundary = trimmed.IndexOf('&', searchValueStart);
            if (boundary < 0 || boundary >= idKeyIndex)
            {
                return null;
            }

            searchText = trimmed[searchValueStart..boundary].Trim();
            recipientId = trimmed[(idKeyIndex + RecipientIdKey.Length)..].Trim();
        }

        if (string.IsNullOrEmpty(recipientId) || string.IsNullOrEmpty(searchText))
        {
            return null;
        }

        return new UsaSpendingFeedTarget(recipientId, searchText);
    }
}
