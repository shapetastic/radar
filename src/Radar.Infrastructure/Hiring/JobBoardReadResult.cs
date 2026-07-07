namespace Radar.Infrastructure.Hiring;

/// <summary>
/// Why an ATS job-board read ended: a board that genuinely has no open roles is <see cref="Success"/>
/// (the result carries zero roles — a valid no-openings board, not an error); every distinct failure mode
/// is its own value so the collector can tell "quiet board" from "dead endpoint". No <c>Forbidden</c>
/// value — these public endpoints need no key/User-Agent (verified by the 2026-07-06 reachability spike);
/// a bad board token yields <see cref="HttpError"/> (HTTP 404).
/// </summary>
internal enum JobBoardReadOutcome
{
    Success,     // board JSON fetched and parsed; the result may still carry zero roles (no openings)
    Unreachable, // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,   // a non-success HTTP status code (incl. a 404 for a bad board token)
    Timeout,     // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,   // JSON could not be parsed, or the expected root/jobs shape was absent
}

/// <summary>
/// Outcome of a single ATS job-board read: a success carrying the normalized <see cref="JobBoardResult"/>,
/// or a failure carrying a short advice-free <see cref="Detail"/> reason used only for logging.
/// </summary>
internal sealed record JobBoardReadResult(
    JobBoardReadOutcome Outcome,
    JobBoardResult? Result,
    string? Detail)
{
    public bool IsSuccess => Outcome == JobBoardReadOutcome.Success;

    public static JobBoardReadResult Success(JobBoardResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(JobBoardReadOutcome.Success, result, Detail: null);
    }

    public static JobBoardReadResult Failure(JobBoardReadOutcome outcome, string detail)
    {
        if (outcome == JobBoardReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Result: null, detail);
    }
}
