namespace Radar.Application.Prices;

/// <summary>
/// Fetches a ticker's daily price history from an external source. This is a SEPARATE seam from
/// <c>IEvidenceCollector</c> by deliberate design (AD-14): price is validation / reference data and must be
/// structurally incapable of becoming <c>CollectedEvidence</c> / a signal / a scoring input. It returns no
/// evidence type, is not in the collector <c>IEnumerable</c> the runner consumes, and its acquisition step runs
/// outside <c>IRadarPipeline</c>. Typed graceful outcomes — the implementation NEVER throws on a bad response;
/// only caller cancellation propagates.
/// </summary>
public interface IPriceHistoryReader
{
    /// <summary>
    /// A short, source-neutral identifier for the reference dataset's origin (e.g. <c>"yahoo-chart-v8"</c>),
    /// stamped onto <see cref="PriceHistory.Source"/> so the acquirer stays source-agnostic (mirrors an
    /// <c>IEvidenceCollector.CollectorName</c>). Advice-free — a raw provenance label, not a recommendation.
    /// </summary>
    string SourceName { get; }

    Task<PriceHistoryReadResult> ReadDailyAsync(string ticker, CancellationToken ct);
}
