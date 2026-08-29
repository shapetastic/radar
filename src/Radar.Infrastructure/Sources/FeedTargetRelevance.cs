namespace Radar.Infrastructure.Sources;

/// <summary>
/// The ONE feed-target relevance predicate shared by the query-driven news collectors (spec 201 §2): true
/// when the whitespace-normalised, case-insensitive article title contains the company query phrase or the
/// (optional) ticker token. It was a byte-equivalent private copy in <c>NewsAttentionCollector</c> and
/// <c>GdeltNewsCollector</c>; spec 200 hardened and pinned only the newssearch copy, so the GDELT copy would
/// have silently missed the next relevance fix. Consolidated here beside <see cref="CollectorCompanyHints"/>
/// and <see cref="QueryFeedTarget"/> (reuse over copy — CLAUDE.md).
/// <para>
/// <b>Share the core, keep the divergent edge per caller.</b> The only genuinely per-source behaviour is
/// the Google News <c>" - Publisher"</c> title-suffix strip the newssearch collector applies BEFORE the
/// check (so a publisher name that happens to contain the ticker/phrase cannot produce a false match). That
/// is passed in as <paramref name="preNormalize"/> rather than folded into the shared rule — GDELT titles
/// carry no such suffix and must not be stripped.
/// </para>
/// <para>
/// Both sides are whitespace-normalised first, so a spaced <c>"( RKLB )"</c> still matches an <c>RKLB</c>
/// ticker and <c>"Rocket Lab USA , Inc ."</c> still matches the <c>Rocket Lab</c> phrase (GDELT spaces out
/// punctuation in titles). The match is an UNANCHORED, case-insensitive <c>Contains</c> — which is exactly
/// why spec 199/200 made certain tickers phrase-only (<c>ITIC</c> in "cr<b>itic</b>", <c>ESQ</c> in
/// "<b>Esq</b>uire") and corrected phrases at the seed; changing the predicate itself needs its own
/// corpus-wide audit.
/// </para>
/// </summary>
internal static class FeedTargetRelevance
{
    /// <param name="title">The article title as the provider supplied it.</param>
    /// <param name="target">The feed's parsed query phrase + optional ticker.</param>
    /// <param name="preNormalize">
    /// An optional per-caller transform applied to the title BEFORE whitespace normalisation (the newssearch
    /// publisher-suffix strip). Null ⇒ the title is used as-is.
    /// </param>
    public static bool IsRelevant(
        string? title, QueryFeedTarget target, Func<string?, string?>? preNormalize = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var normalizedTitle = NormalizeWhitespace(preNormalize is null ? title : preNormalize(title));
        if (normalizedTitle.Length == 0)
        {
            return false;
        }

        var phrase = NormalizeWhitespace(target.QueryPhrase);
        if (phrase.Length > 0
            && normalizedTitle.Contains(phrase, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ticker = NormalizeWhitespace(target.Ticker);
        return ticker.Length > 0
            && normalizedTitle.Contains(ticker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collapses every run of whitespace (spaces, newlines, tabs — <see cref="char.IsWhiteSpace(char)"/>) to
    /// a single space and trims; null/blank becomes empty. Also the collapser
    /// <c>EarningsComparabilityScan</c> runs a stripped filing body through before its phrase match, so a
    /// phrase like "one time" matches across a line break — one definition, three callers.
    /// </summary>
    public static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
