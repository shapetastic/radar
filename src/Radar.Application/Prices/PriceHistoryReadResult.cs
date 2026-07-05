namespace Radar.Application.Prices;

/// <summary>
/// Why a daily price-history read ended: fetched-and-parsed bars are <see cref="Success"/>; every distinct
/// failure mode is its own value so the caller can tell "delisted/unknown ticker" from "dead endpoint".
/// A distinct <see cref="RateLimited"/> (HTTP 429) exists so a throttled source is not confused with a
/// generic <see cref="HttpError"/>. A <see cref="Success"/> may carry ZERO bars (a ticker with no bars in
/// range is a non-error success, not a failure).
/// </summary>
public enum PriceHistoryReadOutcome
{
    Success,        // bars fetched and parsed (may be zero bars)
    Unreachable,    // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,      // a non-success HTTP status (other than 429)
    Timeout,        // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,      // null/empty chart.result, absent arrays, ragged arrays, or unparseable JSON
    RateLimited,    // HTTP 429 — the source is throttling; distinct from a generic HttpError
}

/// <summary>
/// Outcome of a single daily price-history read: a success carrying the parsed <see cref="Bars"/> plus the
/// <see cref="Source"/> label that produced them, or a failure carrying a short advice-free
/// <see cref="Detail"/> reason used only for logging. <see cref="Bars"/> is non-null (possibly empty) on
/// <see cref="PriceHistoryReadOutcome.Success"/> and empty otherwise. This is provider-neutral: the reader
/// NEVER throws on a bad response and reports it as a typed failure here (only caller cancellation
/// propagates).
/// </summary>
public sealed record PriceHistoryReadResult(
    PriceHistoryReadOutcome Outcome,
    IReadOnlyList<PriceBar> Bars,
    string? Source,
    string? Detail)
{
    public bool IsSuccess => Outcome == PriceHistoryReadOutcome.Success;

    public static PriceHistoryReadResult Success(IReadOnlyList<PriceBar> bars, string source)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return new(PriceHistoryReadOutcome.Success, bars, source, Detail: null);
    }

    public static PriceHistoryReadResult Failure(PriceHistoryReadOutcome outcome, string detail)
    {
        if (outcome == PriceHistoryReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Array.Empty<PriceBar>(), Source: null, detail);
    }
}
