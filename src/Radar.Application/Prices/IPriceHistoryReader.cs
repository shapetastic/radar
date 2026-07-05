namespace Radar.Application.Prices;

/// <summary>
/// Fetches a ticker's daily price history from an external source. This is a SEPARATE seam from
/// <c>IEvidenceCollector</c> by deliberate design (AD-14): price is validation/reference data and must be
/// structurally incapable of becoming <c>CollectedEvidence</c> / a signal / a scoring input. Typed graceful
/// outcomes — the implementation NEVER throws on a bad response (it degrades to a typed
/// <see cref="PriceHistoryReadResult"/>); only caller cancellation propagates.
/// </summary>
public interface IPriceHistoryReader
{
    Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct);
}
