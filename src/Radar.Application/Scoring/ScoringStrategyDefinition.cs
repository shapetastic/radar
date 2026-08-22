using Radar.Domain.Signals;

namespace Radar.Application.Scoring;

/// <summary>
/// One <b>scoring strategy</b> (spec 137): a named, already-resolved scoring configuration that Radar can
/// run over a single shared collection pass. A strategy is composition-time data — the profile NAME it was
/// resolved from, the resolved <see cref="ScoringWeights"/>, and whether it is the <b>primary</b> strategy
/// (the one whose snapshots keep the legacy storage location and that the weekly report renders).
/// <para>
/// Deliberately Application-level and <b>already resolved</b>: <c>IConfiguration</c> never reaches
/// <c>Radar.Application</c> (CLAUDE.md layering). Infrastructure binds
/// <c>Radar:Strategies</c> / <c>Radar:Scoring:Profiles:{name}</c> and hands the resolved definitions in.
/// </para>
/// <para>
/// <see cref="Name"/> is the human-readable strategy identity stamped onto every
/// <c>CompanyScoreSnapshot.StrategyName</c>, alongside — never instead of — the opaque
/// <c>ScoringConfigVersion</c> fingerprint. It is deliberately <b>NOT</b> a fingerprint input: two
/// strategies that resolve to the same effective config are genuinely comparable, and adding the name to
/// the hash would move the pinned default fingerprints for no scoring-affecting reason (AD-10).
/// </para>
/// </summary>
/// <param name="Name">The strategy identity (also the non-primary storage directory segment).</param>
/// <param name="ScoringProfile">The <c>Radar:Scoring:Profiles:{name}</c> profile this resolved from.</param>
/// <param name="Weights">The resolved, already-validated scoring magnitudes for this strategy.</param>
/// <param name="IsPrimary">True for the single primary strategy (legacy storage location + reporting).</param>
public sealed record ScoringStrategyDefinition(
    string Name,
    string ScoringProfile,
    ScoringWeights Weights,
    bool IsPrimary)
{
    /// <summary>
    /// The <see cref="SignalType"/>s this strategy consumes (spec 138) — the strategy's <b>hypothesis</b>
    /// about which signals matter, as opposed to <see cref="Weights"/>, which is only about magnitudes.
    /// Defaults to <see cref="SignalTypeFilter.All"/>, so every existing construction site and every existing
    /// config is unchanged (an omitted, empty, or exhaustive <c>SignalTypes</c> all canonicalise onto
    /// <see cref="SignalTypeFilter.All"/>, which hashes to the byte-identical default fingerprint).
    /// <para>
    /// Deliberately an init-only property rather than a positional parameter: it is an additive, defaulted
    /// aspect of a strategy (mirroring <c>ScoringInput.PreCollapseSignals</c>), so every caller that does not
    /// care keeps compiling and keeps the default behaviour.
    /// </para>
    /// <para>
    /// Unlike <see cref="Name"/>, this IS a fingerprint input: two strategies consuming different signal sets
    /// are genuinely different scorings and must never share a <c>ScoringConfigVersion</c>. The fold happens
    /// inside <see cref="ScoringEngine"/> (via <see cref="SignalTypeFilter.Describe"/> over the signal-source
    /// descriptor) so the behavioural gate and the hashed identity can never drift apart.
    /// </para>
    /// </summary>
    public SignalTypeFilter SignalTypes { get; init; } = SignalTypeFilter.All;

    /// <summary>
    /// The <c>radar-formula-vN</c> this strategy scores with (spec 146). Defaults to
    /// <see cref="ScoreFormulaVersions.V8"/>, so <b>a strategy that does not name a formula is
    /// byte-identical to today</b> and the pinned default fingerprints do not move. Resolved through
    /// <see cref="IScoreFormulaFactory"/>; an unknown name fails fast at startup naming the strategy.
    /// <para>
    /// Additive and init-only for the same reason as <see cref="SignalTypes"/>: every existing construction
    /// site keeps compiling and keeps today's behaviour. Which formula produced a score has always been part
    /// of the <c>ScoringConfigVersion</c> fingerprint (<c>_formula.Version</c> is a hashed field), so nothing
    /// new had to be added to the hash for this to re-stamp correctly.
    /// </para>
    /// </summary>
    public string Formula { get; init; } = ScoreFormulaVersions.V8;

    /// <summary>
    /// The weighted channel budget this strategy allocates its score across (spec 146). Defaults to
    /// <see cref="ScoringChannelSet.Empty"/>, which folds into the fingerprint as a NO-OP — so a strategy
    /// that declares no channels hashes exactly what it hashed before this property existed.
    /// <para>
    /// Required by (and only meaningful to) <see cref="ScoreFormulaVersions.V9"/>;
    /// <see cref="ScoringStrategySet"/> rejects a v9 strategy with no channels and a non-v9 strategy that
    /// declares them, so a channel array can never be silently ignored. Like <see cref="SignalTypes"/> — and
    /// unlike <see cref="Name"/> — it IS a fingerprint input: two strategies allocating their score
    /// differently are genuinely different scorings.
    /// </para>
    /// </summary>
    public ScoringChannelSet Channels { get; init; } = ScoringChannelSet.Empty;

    /// <summary>
    /// The declared reporting purpose of this strategy (spec 176): <see cref="StrategyPurpose.Research"/>
    /// (the default — a genuine hypothesis) or <see cref="StrategyPurpose.Comparator"/> (a diagnostic
    /// baseline that exists to be beaten, AD-15). Additive and init-only for the same reason as
    /// <see cref="SignalTypes"/>: every existing construction site keeps compiling and keeps today's
    /// behaviour.
    /// <para>
    /// Unlike <see cref="SignalTypes"/>/<see cref="Channels"/> — and like <see cref="Name"/> — it is
    /// deliberately <b>NOT</b> a fingerprint input: purpose is report metadata, not a scoring-affecting
    /// choice, so two strategies differing only in purpose are the SAME effective scoring and must share a
    /// <c>ScoringConfigVersion</c>. A purpose-only edit therefore moves no fingerprint, forks no series and
    /// never trips <c>StrategyIdentityGuard</c>. <see cref="ScoringStrategySet"/> rejects a Comparator
    /// PRIMARY: the primary owns the Radar narrative and labels, so a comparator primary would make the
    /// config say two contradictory things.
    /// </para>
    /// </summary>
    public StrategyPurpose Purpose { get; init; } = StrategyPurpose.Research;
}
