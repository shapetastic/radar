namespace Radar.Application.Prices;

/// <summary>
/// The opt-in price-history acquisition step (AD-14): enumerate the seeded watch universe, read each ticker's
/// daily bars, and persist them to the reference store. Deliberately a SEPARATE seam from the pipeline — it is
/// invoked by the Worker OUTSIDE <c>IRadarPipeline</c> (the collect → map → resolve → review → store → score →
/// report path) and has no dependency on the evidence/signal/scoring types, so a price bar can never enter
/// <c>CollectAsync</c> / <c>CollectedEvidence</c>.
/// </summary>
public interface IPriceHistoryAcquirer
{
    Task AcquireAsync(CancellationToken ct);
}
