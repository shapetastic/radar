namespace Radar.Infrastructure.Rss;

/// <summary>
/// Why a feed read ended: a genuinely quiet but valid feed is <see cref="Success"/> (Items may be
/// empty); every distinct failure mode is its own value so the collector can tell "quiet week" from
/// "dead feed".
/// </summary>
internal enum RssFeedReadOutcome
{
    Success,      // feed fetched and parsed; Items may still be empty (a genuinely quiet feed)
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // XML could not be parsed, or SyndicationFeed.Load returned null
}

/// <summary>
/// Outcome of a single feed read: a success carrying the parsed items, or a failure carrying a short
/// advice-free <see cref="Detail"/> reason used only for logging.
/// </summary>
internal sealed record RssFeedReadResult(
    RssFeedReadOutcome Outcome,
    IReadOnlyList<RssFeedItem> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == RssFeedReadOutcome.Success;

    public static RssFeedReadResult Success(IReadOnlyList<RssFeedItem> items) =>
        new(RssFeedReadOutcome.Success, items, Detail: null);

    public static RssFeedReadResult Failure(RssFeedReadOutcome outcome, string detail) =>
        new(outcome, [], detail);
}
