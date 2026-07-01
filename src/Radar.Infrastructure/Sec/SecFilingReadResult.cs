namespace Radar.Infrastructure.Sec;

/// <summary>
/// Why a SEC submissions read ended: a genuinely quiet but valid issuer (e.g. a delisted filer with no
/// recent signal-form filings) is <see cref="Success"/> (Items may be empty); every distinct failure
/// mode is its own value so the collector can tell "quiet issuer" from "dead endpoint". A distinct
/// <see cref="Forbidden"/> (HTTP 403) exists because that is almost always a missing/invalid
/// User-Agent, which SEC requires.
/// </summary>
internal enum SecFilingReadOutcome
{
    Success,      // submissions JSON fetched and parsed; Items may still be empty (a quiet/delisted issuer)
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code (other than 403)
    Forbidden,    // HTTP 403 — almost always a missing/invalid User-Agent (SEC mandates a compliant UA)
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // JSON could not be parsed, or the expected submissions shape was absent
}

/// <summary>
/// Outcome of a single submissions read: a success carrying the parsed filings (newest-first), or a
/// failure carrying a short advice-free <see cref="Detail"/> reason used only for logging.
/// </summary>
internal sealed record SecFilingReadResult(
    SecFilingReadOutcome Outcome,
    IReadOnlyList<SecFilingItem> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == SecFilingReadOutcome.Success;

    public static SecFilingReadResult Success(IReadOnlyList<SecFilingItem> items) =>
        new(SecFilingReadOutcome.Success, items, Detail: null);

    public static SecFilingReadResult Failure(SecFilingReadOutcome outcome, string detail)
    {
        if (outcome == SecFilingReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, [], detail);
    }
}
