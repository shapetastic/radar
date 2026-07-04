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
/// </summary>
internal sealed record NewsSearchReadResult(
    NewsSearchReadOutcome Outcome,
    IReadOnlyList<NewsArticleItem> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == NewsSearchReadOutcome.Success;

    public static NewsSearchReadResult Success(IReadOnlyList<NewsArticleItem> items) =>
        new(NewsSearchReadOutcome.Success, items, Detail: null);

    public static NewsSearchReadResult Failure(NewsSearchReadOutcome outcome, string detail)
    {
        if (outcome == NewsSearchReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, [], detail);
    }
}
