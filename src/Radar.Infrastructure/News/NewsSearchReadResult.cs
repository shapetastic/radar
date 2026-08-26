namespace Radar.Infrastructure.News;

/// <summary>
/// Why a Google News RSS search read ended: a company that genuinely has no recent coverage is
/// <see cref="Success"/> (Items may be empty); every distinct failure mode is its own value so spec 81's
/// collector can tell "quiet company" from "dead endpoint" from a throttled response. This mirrors the GDELT
/// reader's outcome set (<c>GdeltReadOutcome</c>) so spec 81's collector degradation logic is a straight port.
/// <see cref="RateLimited"/> exists because a source may return HTTP 429; unlike GDELT's per-IP DOC-API quota,
/// Google News RSS is NOT per-IP throttled (back-to-back requests succeed keyless, verified from this
/// environment), but a 429 remains a distinct outcome the reader degrades to no evidence, never crashing.
/// </summary>
internal enum NewsSearchReadOutcome
{
    Success,      // RSS fetched and parsed; Items may still be empty (a company with no coverage)
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code other than 429
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // XML could not be parsed, or the root was not the expected <rss>/<channel> shape
    RateLimited,  // HTTP 429 Too Many Requests — degraded to no articles emitted
}

/// <summary>
/// Outcome of a single Google News RSS search read: a success carrying the parsed articles (in feed order),
/// or a failure carrying a short advice-free <see cref="Detail"/> reason used only for logging.
/// <para>
/// <b>Spec 190 — local retention limit, NOT a provider ceiling.</b> <see cref="Items"/> is the RETAINED
/// PREFIX: the first <c>MaxRecords</c> structurally valid items of the response, exactly as before. The
/// trailing members describe the rest of the SAME already-fetched response body — nothing is requested,
/// paged or followed to produce them. Reaching the requested limit means Radar stopped retaining at its own
/// EFFECTIVE/LOCAL limit; it is never evidence that the provider had no more to give, and even a scan that
/// observes no item beyond the limit cannot prove the provider's result set was complete (the response
/// itself is whatever the provider chose to return). The three honest states a caller can distinguish are
/// therefore: <i>possible truncation</i> (the prefix filled), <i>confirmed local truncation</i>
/// (<see cref="ObservedValidItemBeyondLocalLimit"/> — a valid item really was discarded by Radar's own
/// limit), and <i>below limit</i>.
/// </para>
/// </summary>
/// <param name="Outcome">Why the read ended.</param>
/// <param name="Items">The retained prefix, in feed order — the ONLY items a collector may admit as evidence or observations.</param>
/// <param name="Detail">Short advice-free failure reason; <c>null</c> on success.</param>
/// <param name="ValidItemsObserved">
/// How many structurally valid (link-bearing) items were observed in the response, bounded by the reader's
/// absolute safety ceiling. Always &gt;= <c>Items.Count</c>. A failure records 0.
/// </param>
/// <param name="DiagnosticTail">
/// The valid items observed BEYOND the retained prefix, in feed order — bounded by the same absolute ceiling
/// and exposed for DIAGNOSTICS ONLY. A collector may count them; it must never map one to evidence, an
/// observation candidate or any scoring input.
/// </param>
internal sealed record NewsSearchReadResult(
    NewsSearchReadOutcome Outcome,
    IReadOnlyList<NewsArticleItem> Items,
    string? Detail,
    int ValidItemsObserved,
    IReadOnlyList<NewsArticleItem> DiagnosticTail)
{
    public bool IsSuccess => Outcome == NewsSearchReadOutcome.Success;

    /// <summary>
    /// CONFIRMED LOCAL TRUNCATION: at least one structurally valid item was observed in the response beyond
    /// Radar's own retention limit, so the discard is a fact rather than a suspicion. Derived (never stored
    /// twice) so it cannot drift from <see cref="ValidItemsObserved"/> / <see cref="DiagnosticTail"/>.
    /// <b>It says nothing about the provider:</b> false means "Radar observed no item beyond its limit", not
    /// "the provider had nothing more".
    /// </summary>
    public bool ObservedValidItemBeyondLocalLimit => ValidItemsObserved > Items.Count;

    /// <summary>
    /// A success with NO diagnostic tail recorded: the observed count degrades to the retained count and the
    /// tail is empty. Kept so a caller (notably a test fake) that only has items stays valid — it records
    /// "no item observed beyond the limit", which is exactly what an unscanned response can honestly claim.
    /// </summary>
    public static NewsSearchReadResult Success(IReadOnlyList<NewsArticleItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(NewsSearchReadOutcome.Success, items, Detail: null, items.Count, []);
    }

    /// <summary>
    /// A success carrying the spec-190 bounded diagnostic scan of the SAME response: the retained prefix,
    /// how many valid items the whole (ceiling-bounded) response held, and the valid items beyond the prefix.
    /// The two diagnostics must reconcile with the prefix — a mismatch is a programming error, not a data
    /// state, so it throws rather than persisting an incoherent pair.
    /// </summary>
    public static NewsSearchReadResult Success(
        IReadOnlyList<NewsArticleItem> items,
        int validItemsObserved,
        IReadOnlyList<NewsArticleItem> diagnosticTail)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(diagnosticTail);

        if (validItemsObserved < items.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validItemsObserved),
                validItemsObserved,
                "The observed valid-item count cannot be smaller than the retained prefix it contains.");
        }

        if (diagnosticTail.Count != validItemsObserved - items.Count)
        {
            throw new ArgumentException(
                "The diagnostic tail must hold exactly the valid items observed beyond the retained prefix.",
                nameof(diagnosticTail));
        }

        return new(NewsSearchReadOutcome.Success, items, Detail: null, validItemsObserved, diagnosticTail);
    }

    public static NewsSearchReadResult Failure(NewsSearchReadOutcome outcome, string detail)
    {
        if (outcome == NewsSearchReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        // A failure observed nothing and carries no diagnostics: there is no response to have scanned.
        return new(outcome, [], detail, ValidItemsObserved: 0, DiagnosticTail: []);
    }
}
