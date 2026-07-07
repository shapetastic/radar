using Radar.Infrastructure.Sources;

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
    /// blank/malformed. The two-key split routes through the shared <see cref="TwoKeyFeedToken"/>; the
    /// both-keys-required and no-blank-value semantics stay this parser's own hooks.
    /// </summary>
    public static UsaSpendingFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();

        // The shared order-robust split (first '&' between the two keys, so the recipient name's own
        // characters — which never contain '&' in our seeds — stay intact).
        if (!TwoKeyFeedToken.TrySplit(
                trimmed, RecipientIdKey, SearchTextKey, out var recipientId, out var searchText))
        {
            return null;
        }

        if (string.IsNullOrEmpty(recipientId) || string.IsNullOrEmpty(searchText))
        {
            return null;
        }

        return new UsaSpendingFeedTarget(recipientId, searchText);
    }
}
