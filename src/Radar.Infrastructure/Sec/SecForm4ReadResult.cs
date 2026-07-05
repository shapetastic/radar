namespace Radar.Infrastructure.Sec;

/// <summary>
/// Why a SEC Form 4 read ended. Mirrors the <see cref="SecFilingReadOutcome"/> value set: a genuinely quiet
/// but valid issuer (no recent Form 4s, or every Form 4 skipped) is <see cref="Success"/> (Items may be
/// empty); every distinct failure mode is its own value so the collector can tell "quiet issuer" from
/// "dead endpoint". A distinct <see cref="Forbidden"/> (HTTP 403) exists because that is almost always a
/// missing/invalid User-Agent, which SEC requires. These outcomes describe the SUBMISSIONS-level read; a
/// single bad Form 4 XML is skipped inside a <see cref="Success"/> without degrading the whole feed.
/// </summary>
internal enum SecForm4ReadOutcome
{
    Success,      // submissions JSON fetched and parsed; Items may still be empty (a quiet issuer or all-skipped)
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code (other than 403)
    Forbidden,    // HTTP 403 — almost always a missing/invalid User-Agent (SEC mandates a compliant UA)
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // JSON could not be parsed, or the expected submissions shape was absent
}

/// <summary>
/// Outcome of a single Form 4 submissions read: a success carrying the parsed, classified filings
/// (newest-first), or a failure carrying a short advice-free <see cref="Detail"/> reason used only for
/// logging. Mirrors <see cref="SecFilingReadResult"/> precisely.
/// </summary>
internal sealed record SecForm4ReadResult(
    SecForm4ReadOutcome Outcome,
    IReadOnlyList<SecForm4Filing> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == SecForm4ReadOutcome.Success;

    public static SecForm4ReadResult Success(IReadOnlyList<SecForm4Filing> items) =>
        new(SecForm4ReadOutcome.Success, items, Detail: null);

    public static SecForm4ReadResult Failure(SecForm4ReadOutcome outcome, string detail)
    {
        if (outcome == SecForm4ReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, [], detail);
    }
}
