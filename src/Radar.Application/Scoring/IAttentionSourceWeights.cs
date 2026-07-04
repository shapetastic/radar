namespace Radar.Application.Scoring;

/// <summary>
/// Per-publisher attention-breadth weighting for the scoring formula's reach term. Injected into
/// <see cref="RadarScoreFormulaV4"/> so the curated "what counts as genuine market notice" policy lives as
/// Infrastructure config data (AD-5) while the formula stays a pure function of its input plus this immutable
/// lookup (AD-3).
/// </summary>
public interface IAttentionSourceWeights
{
    /// <summary>
    /// The attention-breadth weight for a third-party publisher SourceName, in [0,1].
    /// 1.0 = genuine market notice (Reuters, Bloomberg, WSJ, CNBC, AP, industry trades);
    /// low/zero = algorithmic content-mill / aggregator (MarketBeat, Zacks, ...);
    /// an UNKNOWN publisher returns the configured default (non-zero) so real coverage is never
    /// silently zeroed. Case-insensitive; blank/null returns the unknown default.
    /// </summary>
    double WeightFor(string? sourceName);
}
