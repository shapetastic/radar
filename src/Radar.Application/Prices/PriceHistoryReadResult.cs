namespace Radar.Application.Prices;

/// <summary>
/// Why a daily price-history read ended: a fetched-and-parsed series is <see cref="Success"/> (which MAY carry
/// zero bars — a ticker with no bars in range is a non-error outcome); every distinct failure mode is its own
/// value so a caller can tell "unreachable endpoint" from "malformed response" from "rate limited". Mirrors the
/// SEC/News reader outcome enums (a separate seam from evidence, AD-14).
/// </summary>
public enum PriceHistoryReadOutcome
{
    Success,        // the chart document parsed; Bars carries the aligned bars (possibly zero)
    Unreachable,    // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,      // a non-success HTTP status (other than 429)
    Timeout,        // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,      // 200 whose chart.result is null/empty, or whose arrays are absent/ragged
    RateLimited,    // HTTP 429 — distinct so a caller can pace/back off
}

/// <summary>
/// Outcome of a single daily price-history read: a success carrying the parsed <see cref="Bars"/> (possibly
/// empty), or a failure carrying a short advice-free <see cref="Detail"/> reason used only for logging. The
/// reader NEVER throws on a bad response — it returns a typed failure here; only caller cancellation propagates.
/// <see cref="Bars"/> is non-null on <see cref="PriceHistoryReadOutcome.Success"/> and empty otherwise.
/// </summary>
public sealed record PriceHistoryReadResult(
    PriceHistoryReadOutcome Outcome,
    IReadOnlyList<PriceBar> Bars,
    string? Detail)
{
    public bool IsSuccess => Outcome == PriceHistoryReadOutcome.Success;

    public static PriceHistoryReadResult Success(IReadOnlyList<PriceBar> bars) =>
        new(PriceHistoryReadOutcome.Success, bars ?? Array.Empty<PriceBar>(), Detail: null);

    public static PriceHistoryReadResult Failure(PriceHistoryReadOutcome outcome, string detail)
    {
        if (outcome == PriceHistoryReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Array.Empty<PriceBar>(), detail);
    }
}
