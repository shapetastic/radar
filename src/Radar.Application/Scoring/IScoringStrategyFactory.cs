namespace Radar.Application.Scoring;

/// <summary>
/// Produces exactly one configured <see cref="IScoringEngine"/> per <see cref="ScoringStrategyDefinition"/>
/// in the resolved <see cref="ScoringStrategySet"/> (spec 137). The runtimes are built ONCE and cached, so
/// every strategy's <c>ScoringConfigVersion</c> fingerprint is computed once per process exactly as the
/// single-engine composition did.
/// </summary>
public interface IScoringStrategyFactory
{
    /// <summary>
    /// The per-strategy runtimes in the configured (deterministic, AD-3) order. Never empty.
    /// </summary>
    IReadOnlyList<ScoringStrategyRuntime> Runtimes { get; }

    /// <summary>
    /// The primary strategy's runtime — the engine whose snapshots keep the legacy storage location and
    /// whose score repository the weekly report renders.
    /// </summary>
    ScoringStrategyRuntime Primary { get; }
}
