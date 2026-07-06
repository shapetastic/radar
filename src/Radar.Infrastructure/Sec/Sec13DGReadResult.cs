namespace Radar.Infrastructure.Sec;

/// <summary>
/// Why a SEC Schedule 13D/13G read ended. Mirrors <see cref="SecForm4ReadOutcome"/> exactly: a genuinely
/// quiet but valid issuer (no recent 13D/13G filings) is <see cref="Success"/> (Items may be empty); every
/// distinct failure mode is its own value so the collector can tell "quiet issuer" from "dead endpoint". A
/// distinct <see cref="Forbidden"/> (HTTP 403) exists because that is almost always a missing/invalid
/// User-Agent, which SEC requires. These outcomes describe the SUBMISSIONS-level read; unlike Form 4 there is
/// no per-filing body fetch (v1 is metadata-only), so there is no single-bad-filing skip.
/// </summary>
internal enum Sec13DGReadOutcome
{
    Success,      // submissions JSON fetched and parsed; Items may still be empty (a quiet issuer)
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code (other than 403)
    Forbidden,    // HTTP 403 — almost always a missing/invalid User-Agent (SEC mandates a compliant UA)
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // JSON could not be parsed, or the expected submissions shape was absent
}

/// <summary>
/// Outcome of a single SEC 13D/13G submissions read: a success carrying the parsed, classified filings
/// (newest-first), or a failure carrying a short advice-free <see cref="Detail"/> reason used only for
/// logging. Mirrors <see cref="SecForm4ReadResult"/> precisely.
/// </summary>
internal sealed record Sec13DGReadResult(
    Sec13DGReadOutcome Outcome,
    IReadOnlyList<Sec13DGFiling> Items,
    string? Detail)
{
    public bool IsSuccess => Outcome == Sec13DGReadOutcome.Success;

    public static Sec13DGReadResult Success(IReadOnlyList<Sec13DGFiling> items) =>
        new(Sec13DGReadOutcome.Success, items, Detail: null);

    public static Sec13DGReadResult Failure(Sec13DGReadOutcome outcome, string detail)
    {
        if (outcome == Sec13DGReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, [], detail);
    }
}
