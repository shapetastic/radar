namespace Radar.Infrastructure.Fcc;

/// <summary>
/// A single FCC equipment authorization normalized from the OET EAS GenericSearch result: its public
/// <see cref="FccId"/> (grantee code + product code concatenated), a short equipment/product
/// <see cref="Description"/> (the EAS equipment-class label), and the <see cref="GrantDate"/>. The description
/// is carried for the bounded metadata provenance sample ONLY and is NEVER placed in the evidence
/// Title/RawText (a raw product description could trip unrelated keyword rules — the same no-contamination
/// discipline as the patent collector's grant titles).
/// </summary>
internal sealed record EquipmentAuthorization(string FccId, string Description, DateOnly GrantDate);

/// <summary>
/// The parsed result of one bounded EAS GenericSearch page: <see cref="GrantCount"/> is the authoritative,
/// deterministic count of authorization rows parsed from the returned CSV (the count the evidence reports);
/// <see cref="Grants"/> are the parsed authorizations (used for the bounded sample metadata). A source-reported
/// grand total is not exposed by the CSV export, so it is intentionally omitted (unlike the patents reader's
/// <c>total_hits</c>). <see cref="Truncated"/> is set when the page cap (<c>MaxPageSize</c>) was hit while at
/// least one further valid authorization remained — so <see cref="GrantCount"/> is a floor ("at least N"),
/// not an exact count. Downstream (and the deferred slice-B surge detection) must treat a truncated snapshot
/// as a lower bound rather than a real total.
/// </summary>
internal sealed record FccAuthResult(
    int GrantCount, IReadOnlyList<EquipmentAuthorization> Grants, bool Truncated = false);

/// <summary>
/// Why an FCC EAS equipment-authorization read ended: a grantee that genuinely has no recent authorizations is
/// <see cref="Success"/> (Grants may be empty); every distinct failure mode is its own value so the collector
/// can tell "no recent grants" from "dead endpoint". The EAS GenericSearch export needs NO API key (a free
/// public database), so there is no MissingApiKey member.
/// </summary>
internal enum FccAuthOutcome
{
    Success,       // CSV fetched and parsed; Grants may still be empty (a grantee with no recent authorizations)
    Unreachable,   // transport error (HttpRequestException — DNS, connection refused, TLS, Akamai edge reset, etc.)
    HttpError,     // a non-success HTTP status code (e.g. an Akamai 403 from a datacenter IP)
    Timeout,       // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,     // the CSV could not be parsed, or the expected header columns were absent
}

/// <summary>
/// Outcome of a single FCC EAS read: a success carrying the parsed authorizations, or a failure carrying a
/// short advice-free <see cref="Detail"/> reason used only for logging.
/// </summary>
internal sealed record FccAuthReadResult(
    FccAuthOutcome Outcome,
    FccAuthResult? Result,
    string? Detail)
{
    public bool IsSuccess => Outcome == FccAuthOutcome.Success;

    public static FccAuthReadResult Success(FccAuthResult result) =>
        new(FccAuthOutcome.Success, result, Detail: null);

    public static FccAuthReadResult Failure(FccAuthOutcome outcome, string detail)
    {
        if (outcome == FccAuthOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Result: null, detail);
    }
}
