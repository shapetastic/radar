namespace Radar.Application.Prices;

/// <summary>
/// Why a daily price-history read ended: a fetched-and-parsed series is <see cref="Success"/> (which MAY carry
/// zero bars — a ticker with no bars in range is a success with nothing, not an error); every distinct failure
/// mode is its own value so the caller can tell a transient transport blip from a bad/changed response. A 429
/// is a distinct <see cref="RateLimited"/> so it is never conflated with a generic <see cref="HttpError"/>.
/// </summary>
public enum PriceHistoryReadOutcome
{
    Success,        // the chart document parsed; Bars carries 0..N daily bars (0 = quiet ticker, not an error)
    Unreachable,    // transport error (HttpRequestException — DNS, connection refused, TLS, etc.)
    HttpError,      // a non-success HTTP status (other than 429)
    Timeout,        // the request's own HTTP deadline elapsed (TaskCanceledException, ct NOT requested)
    Malformed,      // 200 but chart.result null/empty, arrays absent, or OHLCV arrays ragged (length mismatch)
    RateLimited,    // HTTP 429 — the endpoint asked us to back off
}

/// <summary>
/// Outcome of a single daily price-history read: a success carrying the parsed <see cref="Bars"/>, or a failure
/// carrying a short advice-free <see cref="Detail"/> reason used only for logging. Mirrors the SEC/News typed
/// graceful-outcome readers — the reader NEVER throws on a bad response; only caller cancellation propagates.
/// </summary>
public sealed record PriceHistoryReadResult(
    PriceHistoryReadOutcome Outcome,
    IReadOnlyList<PriceBar> Bars,
    string? Detail)
{
    public bool IsSuccess => Outcome == PriceHistoryReadOutcome.Success;

    public static PriceHistoryReadResult Success(IReadOnlyList<PriceBar> bars) =>
        new(PriceHistoryReadOutcome.Success, bars, Detail: null);

    public static PriceHistoryReadResult Failure(PriceHistoryReadOutcome outcome, string detail)
    {
        if (outcome == PriceHistoryReadOutcome.Success)
            throw new ArgumentException("A failure result cannot carry the Success outcome.", nameof(outcome));

        return new(outcome, Array.Empty<PriceBar>(), detail);
    }
}
