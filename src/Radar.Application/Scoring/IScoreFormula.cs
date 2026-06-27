namespace Radar.Application.Scoring;

/// <summary>
/// The scoring formula seam — <b>the human-owned boundary of Stage 6</b>. The implementation defines
/// HOW raw signals become the five component scores; this is the product-owned decision (weights,
/// thresholds, exact computation) that the maintainer owns. The scoring engine (task 15) depends only
/// on this interface and never on a concrete formula, so the real formula can be dropped in without
/// touching any other Stage 6 infrastructure.
///
/// Contract for any implementation:
///  - Pure and deterministic: the same <see cref="ScoringInput"/> MUST yield an equivalent
///    <see cref="ScoreComputation"/> — the same component scores, <c>ComponentJson</c>, explanation,
///    and the same contributions in the same order. (This is value/content equivalence, not record
///    <c>Equals</c>: <see cref="ScoreComputation"/> carries a contributions list, so reference-based
///    record equality is not implied.) No I/O, no clock, no randomness.
///  - Every component score MUST be within the inclusive range 0..100.
///  - <see cref="Version"/> is a stable, explicit formula identity (e.g. "mvp-v1"); change it
///    whenever the computation changes, so snapshots remain reproducible and auditable.
///  - Empty input (no signals) MUST still return a valid computation: in-range components, valid
///    <c>ComponentJson</c>, a non-empty explanation, and an empty contributions list.
///  - Provenance MUST be preserved: every <see cref="ScoreContribution"/> carries both the
///    contributing signal's Id and the evidence Id behind it.
/// </summary>
public interface IScoreFormula
{
    /// <summary>Stable formula version recorded on every score snapshot.</summary>
    string Version { get; }

    /// <summary>Computes the component scores and contributions for the given windowed input.</summary>
    ScoreComputation Compute(ScoringInput input);
}
