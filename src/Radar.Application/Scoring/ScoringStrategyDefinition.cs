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
    bool IsPrimary);
