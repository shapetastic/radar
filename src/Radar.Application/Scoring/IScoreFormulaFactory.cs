namespace Radar.Application.Scoring;

/// <summary>
/// Builds an <see cref="IScoreFormula"/> over a specific set of <see cref="ScoringWeights"/>.
/// <para>
/// The formula HOLDS its magnitudes (see <see cref="RadarScoreFormulaV8"/>'s constructor), so "one engine
/// per strategy" (spec 137) implies "one formula per strategy" — a single shared <see cref="IScoreFormula"/>
/// singleton could only ever express one strategy's weights. This factory is therefore the composition seam
/// the strategy factory uses; the <b>human-owned formula boundary itself is unchanged</b>
/// (<see cref="IScoreFormula"/> keeps its exact contract, and the concrete formula class is still the only
/// place scoring math lives).
/// </para>
/// </summary>
public interface IScoreFormulaFactory
{
    /// <summary>
    /// Returns a pure, deterministic formula bound to <paramref name="weights"/>. Implementations must fail
    /// fast (<see cref="InvalidOperationException"/>) on a nonsensical weight, exactly as direct formula
    /// construction does.
    /// </summary>
    IScoreFormula Create(ScoringWeights weights);
}
