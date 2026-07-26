namespace Radar.Application.Scoring;

/// <summary>
/// Builds the <see cref="IScoreFormula"/> one strategy scores with.
/// <para>
/// The formula HOLDS its configuration (see <see cref="RadarScoreFormulaV8"/>'s and
/// <see cref="RadarScoreFormulaV9"/>'s constructors), so "one engine per strategy" (spec 137) implies "one
/// formula per strategy" — a single shared <see cref="IScoreFormula"/> singleton could only ever express one
/// strategy's configuration. This factory is therefore the composition seam the strategy factory uses; the
/// <b>human-owned formula boundary itself is unchanged</b> (<see cref="IScoreFormula"/> keeps its exact
/// contract, and the concrete formula classes are still the only place scoring math lives).
/// </para>
/// <para>
/// Spec 146 widened the input from bare <see cref="ScoringWeights"/> to the whole
/// <see cref="ScoringStrategyDefinition"/>, because WHICH formula class a strategy runs is now a
/// per-strategy choice (<see cref="ScoringStrategyDefinition.Formula"/>) alongside its magnitudes and its
/// channel budget. Selecting the class from a fixed, closed set stays a code decision (AD-6) — configuring
/// a formula can only pick between shipped <c>radar-formula-vN</c> classes, never define a new one.
/// </para>
/// </summary>
public interface IScoreFormulaFactory
{
    /// <summary>
    /// Returns a pure, deterministic formula bound to <paramref name="definition"/>'s formula version,
    /// magnitudes and channel budget. Implementations must fail fast
    /// (<see cref="InvalidOperationException"/>) on a nonsensical weight, exactly as direct formula
    /// construction does, and on a formula name that is not a shipped <c>radar-formula-vN</c> — with a
    /// message naming the strategy and listing the known formulas.
    /// </summary>
    IScoreFormula Create(ScoringStrategyDefinition definition);
}
