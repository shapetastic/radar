namespace Radar.Infrastructure.News;

/// <summary>
/// The per-company inputs a <c>news</c> feed carries in its single <c>Url</c> field, parsed from the
/// documented token <c>query=&lt;company phrase&gt;&amp;ticker=&lt;TICKER&gt;</c>
/// (e.g. <c>query=Rocket Lab&amp;ticker=RKLB</c>) — the same token shape the GDELT reader uses. This keeps the
/// shared <see cref="Radar.Domain.Companies.CompanySourceFeed"/> record unchanged. <see cref="QueryPhrase"/>
/// is the precise company name sent to Google News RSS; <see cref="Ticker"/> is an optional explicit ticker
/// token spec 81's collector will also match against in its client-side title relevance filter (news search
/// has no exact-entity key). An unparsable/empty token — or one missing the required <c>query=</c> key —
/// yields <see langword="null"/> so the collector can degrade it to a source failure rather than throwing.
/// </summary>
internal sealed record NewsFeedTarget(string QueryPhrase, string? Ticker)
{
    private const string QueryKey = "query=";
    private const string TickerKey = "ticker=";

    /// <summary>
    /// Parses a <c>query=...&amp;ticker=...</c> token. Robust to key ordering and surrounding whitespace;
    /// the phrase's literal spaces are preserved (the token is NOT URL-decoded). The <c>ticker=</c> key is
    /// optional — a bare <c>query=&lt;phrase&gt;</c> token parses with a null ticker. Returns
    /// <see langword="null"/> when the token is blank, the <c>query=</c> key is missing, or the phrase is empty.
    /// </summary>
    public static NewsFeedTarget? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();

        // The query phrase is mandatory; without it the feed cannot be searched.
        var queryKeyIndex = trimmed.IndexOf(QueryKey, StringComparison.Ordinal);
        if (queryKeyIndex < 0)
        {
            return null;
        }

        var tickerKeyIndex = trimmed.IndexOf(TickerKey, StringComparison.Ordinal);

        string phrase;
        string? ticker = null;

        if (tickerKeyIndex < 0)
        {
            // query=<phrase> only — the ticker is optional.
            phrase = trimmed[(queryKeyIndex + QueryKey.Length)..].Trim();
        }
        else if (queryKeyIndex < tickerKeyIndex)
        {
            // query=<phrase>&ticker=<TICKER> — split on the FIRST '&' between the two keys so the phrase's
            // own spaces stay intact (our seeds never put '&' inside a phrase).
            var phraseStart = queryKeyIndex + QueryKey.Length;
            var boundary = trimmed.IndexOf('&', phraseStart);
            if (boundary < 0 || boundary >= tickerKeyIndex)
            {
                return null;
            }

            phrase = trimmed[phraseStart..boundary].Trim();
            ticker = trimmed[(tickerKeyIndex + TickerKey.Length)..].Trim();
        }
        else
        {
            // ticker=<TICKER>&query=<phrase>
            var tickerStart = tickerKeyIndex + TickerKey.Length;
            var boundary = trimmed.IndexOf('&', tickerStart);
            if (boundary < 0 || boundary >= queryKeyIndex)
            {
                return null;
            }

            ticker = trimmed[tickerStart..boundary].Trim();
            phrase = trimmed[(queryKeyIndex + QueryKey.Length)..].Trim();
        }

        if (string.IsNullOrEmpty(phrase))
        {
            return null;
        }

        // An empty ticker value (e.g. "query=X&ticker=") is treated as "no ticker" rather than a hard failure.
        if (string.IsNullOrEmpty(ticker))
        {
            ticker = null;
        }

        return new NewsFeedTarget(phrase, ticker);
    }
}
