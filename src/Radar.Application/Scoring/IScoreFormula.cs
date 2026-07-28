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

    /// <summary>
    /// The formula's COMPOSITION revision — an opt-in second identity component that closes the hole spec 149
    /// exposed. Default <see cref="string.Empty"/>, which means "not versioned separately"; the composed
    /// identity is then the bare <see cref="Version"/> token.
    /// <para>
    /// <b>WHY IT EXISTS.</b> <c>ScoringEngine</c> hashes a formula's VERSION TOKEN, not its code, so a formula
    /// edited in place without a <c>radar-formula-vN</c> bump re-stamps nothing. Spec 149 did exactly that to
    /// <c>radar-formula-v9</c> — it added the notedness discount to v9's composition while leaving the token
    /// and the default <see cref="ScoringWeights"/> untouched — so v9 snapshots written before and after it
    /// are falsely comparable and <c>StrategyIdentityGuard</c> cannot see the difference. This member makes
    /// that failure mode UNREACHABLE for any formula that opts in: bumping the revision moves the strategy's
    /// <c>ScoringConfigVersion</c>, which trips the guard on the next run.
    /// </para>
    /// <para>
    /// <b>Its relationship to AD-6, stated so it cannot be misread as a loophole:</b> a genuinely NEW
    /// structure still earns a new <c>radar-formula-vN</c> class and token. The revision exists so that an
    /// in-place ADJUSTMENT to an existing structure — the spec-149 shape — cannot happen invisibly; it is not
    /// a licence to keep amending one formula forever.
    /// </para>
    /// <para>
    /// It is a DEFAULT interface member on purpose: <c>radar-formula-v8</c> and <c>radar-formula-v9</c> do not
    /// override it, so their composed identity, their persisted <c>EffectiveScoringConfig.FormulaVersion</c>,
    /// their <c>ScoringVersion</c> stamp and every pinned fingerprint are byte-identical to before it existed.
    /// Compose it through <see cref="FormulaIdentity.Of"/> — never by hand — so the three places the engine
    /// stamps it cannot drift.
    /// </para>
    /// </summary>
    string CompositionRevision => string.Empty;

    /// <summary>Computes the component scores and contributions for the given windowed input.</summary>
    ScoreComputation Compute(ScoringInput input);
}
