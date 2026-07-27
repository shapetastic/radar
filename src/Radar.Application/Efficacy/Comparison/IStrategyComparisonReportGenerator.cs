namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// The opt-in strategy-vs-price comparison step (spec 140, AD-14 read side): reads each configured strategy's
/// persisted score series, joins it to price, ranks the strategies with a chronological hold-out, and writes
/// the leaderboard CSV + markdown. Runs as a Worker step DISTINCT from and OUTSIDE <c>IRadarPipeline</c>, after
/// the per-company efficacy render. It reads score history + price and writes only efficacy artifacts; it never
/// feeds evidence, signals, or scoring, and it promotes nothing — ranking is not acting.
/// </summary>
public interface IStrategyComparisonReportGenerator
{
    /// <summary>Builds and writes the leaderboard; returns it so a caller can assert on the result.</summary>
    Task<StrategyLeaderboard> GenerateAsync(CancellationToken ct);
}
