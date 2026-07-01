namespace Radar.Infrastructure.UsaSpending;

/// <summary>
/// Why a USASpending <c>spending_by_award</c> read ended: a recipient that genuinely has no matching
/// awards is <see cref="Success"/> (Items may be empty); every distinct failure mode is its own value so
/// the collector can tell "quiet recipient" from "dead endpoint" from the provenance-critical
/// <see cref="FiltersIgnored"/> case. <see cref="FiltersIgnored"/> exists because an unsupported filter
/// key is SILENTLY dropped by the API and the entire national firehose is returned — that must be a hard
/// failure, never emitted as evidence.
/// </summary>
internal enum UsaSpendingReadOutcome
{
    Success,        // awards JSON fetched and parsed; Items may still be empty (a recipient with no awards)
    Unreachable,    // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,      // a non-success HTTP status code (incl. a 400 award-type-group validation error)
    Timeout,        // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,      // JSON could not be parsed, or the expected results shape was absent
    FiltersIgnored, // messages[] warned a filter was "not used" — the silent firehose; no awards emitted
}

/// <summary>
/// Outcome of a single <c>spending_by_award</c> read: a success carrying the parsed awards (sorted by
/// amount desc as requested), or a failure carrying a short advice-free <see cref="Detail"/> reason used
/// only for logging.
/// </summary>
internal sealed record UsaSpendingReadResult(
    UsaSpendingReadOutcome Outcome,
    IReadOnlyList<UsaSpendingAwardItem> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == UsaSpendingReadOutcome.Success;

    public static UsaSpendingReadResult Success(IReadOnlyList<UsaSpendingAwardItem> items) =>
        new(UsaSpendingReadOutcome.Success, items, Detail: null);

    public static UsaSpendingReadResult Failure(UsaSpendingReadOutcome outcome, string detail)
    {
        if (outcome == UsaSpendingReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, [], detail);
    }
}
