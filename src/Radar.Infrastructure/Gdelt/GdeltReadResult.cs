namespace Radar.Infrastructure.Gdelt;

/// <summary>
/// Why a GDELT DOC <c>ArtList</c> read ended: a company that genuinely has no recent coverage is
/// <see cref="Success"/> (Items may be empty); every distinct failure mode is its own value so the collector
/// can tell "quiet company" from "dead endpoint" from the operationally-dominant
/// <see cref="RateLimited"/> case. <see cref="RateLimited"/> exists because GDELT throttles hard and returns
/// HTTP 429 for back-to-back requests — it must degrade that feed to no evidence (after an optional bounded
/// retry), never crash the run.
/// </summary>
internal enum GdeltReadOutcome
{
    Success,      // articles JSON fetched and parsed; Items may still be empty (a company with no coverage)
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code other than 429
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // JSON could not be parsed, or the root was not the expected object shape
    RateLimited,  // HTTP 429 Too Many Requests — GDELT's aggressive throttle; no articles emitted
}

/// <summary>
/// Outcome of a single GDELT DOC <c>ArtList</c> read: a success carrying the parsed articles (sorted
/// DateDesc as requested), or a failure carrying a short advice-free <see cref="Detail"/> reason used only
/// for logging.
/// </summary>
internal sealed record GdeltReadResult(
    GdeltReadOutcome Outcome,
    IReadOnlyList<GdeltArticleItem> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == GdeltReadOutcome.Success;

    public static GdeltReadResult Success(IReadOnlyList<GdeltArticleItem> items) =>
        new(GdeltReadOutcome.Success, items, Detail: null);

    public static GdeltReadResult Failure(GdeltReadOutcome outcome, string detail)
    {
        if (outcome == GdeltReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, [], detail);
    }
}
