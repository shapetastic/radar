namespace Radar.Application.Scoring;

/// <summary>
/// One strategy's runnable pairing (spec 137): its <see cref="ScoringStrategyDefinition"/> and the
/// <see cref="IScoringEngine"/> instance configured for it. One engine instance IS one strategy — the
/// engine already resolves its whole effective config (and therefore its <c>ScoringConfigVersion</c>
/// fingerprint) once in its constructor — so no per-call weights and no scoring-core change are required.
/// </summary>
public sealed record ScoringStrategyRuntime(
    ScoringStrategyDefinition Definition,
    IScoringEngine Engine);
