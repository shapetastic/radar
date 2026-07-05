namespace Radar.Application.Prices;

/// <summary>
/// The opt-in daily price-history acquisition step (AD-14). Enumerates the seeded watch universe, reads each
/// ticker's daily bars via <see cref="IPriceHistoryReader"/>, and persists each success via
/// <see cref="IPriceHistoryStore"/>. It runs as a SEPARATE Worker step OUTSIDE <c>IRadarPipeline</c> (the
/// collect→map→resolve→review→store→score→report path) and has NO dependency on evidence/signal/scoring
/// types — price can never enter the evidence pipeline through this seam.
/// </summary>
public interface IPriceHistoryAcquirer
{
    Task AcquireAsync(CancellationToken ct);
}
