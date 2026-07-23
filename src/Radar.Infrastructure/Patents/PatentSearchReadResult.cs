namespace Radar.Infrastructure.Patents;

/// <summary>
/// A single granted patent normalized from the PatentsView Search API: its public grant number
/// (<see cref="PatentId"/>), the granted-invention title (<see cref="Title"/>), and the grant date
/// (<see cref="GrantDate"/>). Titles are carried for the bounded metadata provenance sample ONLY and are
/// NEVER placed in the evidence Title/RawText (a raw patent title could trip unrelated keyword rules — the
/// same no-contamination discipline as the hiring collector's job titles).
/// </summary>
internal sealed record PatentGrant(string PatentId, string Title, DateOnly GrantDate);

/// <summary>
/// The parsed result of one bounded PatentsView page: <see cref="GrantCount"/> is the authoritative,
/// deterministic count of grants parsed from the returned page (the count the evidence reports);
/// <see cref="ApiReportedTotal"/> is the API's own grand total (<c>total_hits</c>) kept only as a metadata
/// cross-check when the API reports more grants than fit the bounded page; <see cref="Grants"/> are the
/// parsed grants (used for the bounded sample-titles metadata).
/// </summary>
internal sealed record PatentSearchResult(
    int GrantCount, int ApiReportedTotal, IReadOnlyList<PatentGrant> Grants);

/// <summary>
/// Why a PatentsView granted-patent read ended: an assignee that genuinely has no recent grants is
/// <see cref="Success"/> (Grants may be empty); every distinct failure mode is its own value so the
/// collector can tell "no recent grants" from "dead endpoint" from the <see cref="MissingApiKey"/> case.
/// <see cref="MissingApiKey"/> is returned WITHOUT an HTTP call when the configured env var is blank — a
/// distinct, clearly-logged degrade, not an exception.
/// </summary>
internal enum PatentSearchOutcome
{
    Success,       // patents JSON fetched and parsed; Grants may still be empty (an assignee with no recent grants)
    Unreachable,   // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,     // a non-success HTTP status code
    Timeout,       // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,     // JSON could not be parsed, or the expected patents shape was absent
    MissingApiKey, // the configured API-key env var is blank/absent — degrade with NO HTTP call
}

/// <summary>
/// Outcome of a single PatentsView read: a success carrying the parsed grants, or a failure carrying a
/// short advice-free <see cref="Detail"/> reason used only for logging.
/// </summary>
internal sealed record PatentSearchReadResult(
    PatentSearchOutcome Outcome,
    PatentSearchResult? Result,
    string? Detail)
{
    public bool IsSuccess => Outcome == PatentSearchOutcome.Success;

    public static PatentSearchReadResult Success(PatentSearchResult result) =>
        new(PatentSearchOutcome.Success, result, Detail: null);

    public static PatentSearchReadResult Failure(PatentSearchOutcome outcome, string detail)
    {
        if (outcome == PatentSearchOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Result: null, detail);
    }
}
