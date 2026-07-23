namespace Radar.Infrastructure.Trademarks;

/// <summary>
/// A single trademark application normalized from the USPTO trademark search API: its public serial number
/// (<see cref="SerialNumber"/>), the mark text/wordmark (<see cref="MarkText"/>), and the filing date
/// (<see cref="FilingDate"/>). Mark texts are carried for the bounded metadata provenance sample ONLY and are
/// NEVER placed in the evidence Title/RawText (a raw wordmark like "LAUNCHPAD" could trip unrelated keyword
/// rules — the same no-contamination discipline as the patents collector's titles).
/// </summary>
internal sealed record TrademarkFiling(string SerialNumber, string MarkText, DateOnly FilingDate);

/// <summary>
/// The parsed result of one bounded USPTO trademark search page: <see cref="FilingCount"/> is the
/// authoritative, deterministic count of filings parsed from the returned page (the count the evidence
/// reports); <see cref="ApiReportedTotal"/> is the API's own grand total kept only as a metadata cross-check
/// when the API reports more filings than fit the bounded page; <see cref="Filings"/> are the parsed filings
/// (used for the bounded sample-marks metadata).
/// </summary>
internal sealed record TrademarkSearchResult(
    int FilingCount, int ApiReportedTotal, IReadOnlyList<TrademarkFiling> Filings);

/// <summary>
/// Why a USPTO trademark search read ended: an owner that genuinely has no recent filings is
/// <see cref="Success"/> (Filings may be empty); every distinct failure mode is its own value so the
/// collector can tell "no recent filings" from "dead endpoint" from the <see cref="MissingApiKey"/> case.
/// <see cref="MissingApiKey"/> is returned WITHOUT an HTTP call when the configured env var is blank — a
/// distinct, clearly-logged degrade, not an exception (the reachable ODP trademark route requires a key).
/// </summary>
internal enum TrademarkSearchOutcome
{
    Success,       // trademark JSON fetched and parsed; Filings may still be empty (an owner with no recent filings)
    Unreachable,   // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,     // a non-success HTTP status code
    Timeout,       // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,     // JSON could not be parsed, or the expected results shape was absent
    MissingApiKey, // the configured API-key env var is blank/absent — degrade with NO HTTP call
}

/// <summary>
/// Outcome of a single USPTO trademark search read: a success carrying the parsed filings, or a failure
/// carrying a short advice-free <see cref="Detail"/> reason used only for logging.
/// </summary>
internal sealed record TrademarkSearchReadResult(
    TrademarkSearchOutcome Outcome,
    TrademarkSearchResult? Result,
    string? Detail)
{
    public bool IsSuccess => Outcome == TrademarkSearchOutcome.Success;

    public static TrademarkSearchReadResult Success(TrademarkSearchResult result) =>
        new(TrademarkSearchOutcome.Success, result, Detail: null);

    public static TrademarkSearchReadResult Failure(TrademarkSearchOutcome outcome, string detail)
    {
        if (outcome == TrademarkSearchOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Result: null, detail);
    }
}
