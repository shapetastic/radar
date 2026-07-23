namespace Radar.Infrastructure.Fda;

/// <summary>
/// A single FDA device clearance/approval normalized from an openFDA device endpoint: its public submission
/// number (<see cref="SubmissionNumber"/> — the 510(k) <c>k_number</c> or the PMA <c>pma_number</c>), the
/// device name (<see cref="DeviceName"/> — the 510(k) <c>device_name</c> or the PMA <c>trade_name</c>), the
/// FDA decision date (<see cref="DecisionDate"/>), and which regulatory track it came from
/// (<see cref="Track"/> = <c>"510(k)"</c> or <c>"PMA"</c>). Device names are carried for the bounded metadata
/// provenance sample ONLY and are NEVER placed in the evidence Title/RawText (a raw device name could trip
/// unrelated keyword rules — the same no-contamination discipline as the patents collector's titles).
/// </summary>
internal sealed record FdaClearance(string SubmissionNumber, string DeviceName, DateOnly DecisionDate, string Track);

/// <summary>
/// The parsed result of one bounded openFDA read across BOTH device endpoints (510(k) + PMA):
/// <see cref="ClearanceCount"/> is the authoritative, deterministic count of clearances parsed from the
/// returned pages (the count the evidence reports); <see cref="Clearances"/> are the parsed clearances (used
/// for the bounded sample-clearances metadata). <see cref="ReportedTotal510k"/> / <see cref="ReportedTotalPma"/>
/// are each endpoint's own <c>meta.results.total</c> kept only as a metadata cross-check when an endpoint
/// reports more clearances than fit the bounded page.
/// </summary>
internal sealed record FdaClearanceResult(
    int ClearanceCount,
    IReadOnlyList<FdaClearance> Clearances,
    int ReportedTotal510k,
    int ReportedTotalPma);

/// <summary>
/// Why an openFDA device-clearance read ended: an applicant that genuinely has no recent clearances is
/// <see cref="Success"/> (Clearances may be empty — including openFDA's documented empty-search 404); every
/// distinct failure mode is its own value so the collector can tell "no recent clearances" from "dead
/// endpoint" from a malformed response. openFDA needs NO API key, so there is no MissingApiKey case.
/// </summary>
internal enum FdaReadOutcome
{
    Success,      // JSON fetched and parsed (or the documented empty-search 404); Clearances may still be empty
    Unreachable,  // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,    // a non-success HTTP status code other than the documented empty-search 404
    Timeout,      // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,    // JSON could not be parsed, or the expected results shape was absent
}

/// <summary>
/// Outcome of a single openFDA device-clearance read: a success carrying the parsed clearances, or a failure
/// carrying a short advice-free <see cref="Detail"/> reason used only for logging.
/// </summary>
internal sealed record FdaClearanceReadResult(
    FdaReadOutcome Outcome,
    FdaClearanceResult? Result,
    string? Detail)
{
    public bool IsSuccess => Outcome == FdaReadOutcome.Success;

    public static FdaClearanceReadResult Success(FdaClearanceResult result) =>
        new(FdaReadOutcome.Success, result, Detail: null);

    public static FdaClearanceReadResult Failure(FdaReadOutcome outcome, string detail)
    {
        if (outcome == FdaReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Result: null, detail);
    }
}
