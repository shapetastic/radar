namespace Radar.Domain.Scoring;

/// <remarks>
/// <see cref="ScoringConfigVersion"/> is DISTINCT from <see cref="ScoringVersion"/>:
/// <see cref="ScoringVersion"/> identifies the engine+formula identity, whereas
/// <see cref="ScoringConfigVersion"/> identifies the whole scoring-affecting generation
/// (formula + extractor rules + materiality tiers + scoring options) as a content fingerprint. Since spec
/// 141 it is recorded provenance and a drift detector — <b>not</b> the comparability key: a <c>null</c>
/// value simply means unknown/pre-stamp (an old on-disk file, or any snapshot written before this field
/// existed).
/// <para>
/// <see cref="StrategyName"/> (spec 137) is the HUMAN-READABLE identity of the scoring strategy that
/// produced this snapshot, carried <b>alongside</b> — never instead of — the opaque
/// <see cref="ScoringConfigVersion"/> fingerprint (fingerprints are unreadable, and two strategies could in
/// principle resolve to the same effective config). It is NOT a fingerprint input. Since spec 141 it IS the
/// SERIES KEY: two snapshots are comparable when they belong to the same strategy
/// (<c>ScoreSeriesKey</c>), because a strategy is immutable by convention (enforced at startup by
/// <c>StrategyIdentityGuard</c>) while the fingerprint moves for reasons that cannot touch a score. A
/// <c>null</c> value means the primary/legacy strategy (an on-disk file written before this field existed, or
/// a snapshot produced outside the strategy composition) and reads as the <c>"default"</c> series.
/// </para>
/// <para>
/// <see cref="CollectionProvenance"/> (spec 141) records WHAT WAS COLLECTED on the run that produced this
/// snapshot — the enabled-collector descriptor
/// (<c>ISignalSourceDescriptor.CollectionProvenance()</c>), verbatim. It is <b>recorded, never hashed</b>:
/// it is not a fingerprint input and not a comparability input, because "which collectors ran" and "which
/// hypothesis produced this score" are different facts with different lifetimes (AD-10 as amended by spec
/// 141). Enabling a collector that a strategy consumes nothing from changes this field and nothing else. A
/// <c>null</c> value means unknown/pre-stamp (an on-disk file written before this field existed).
/// </para>
/// </remarks>
public sealed record CompanyScoreSnapshot(
    Guid Id,
    Guid CompanyId,
    string ScoringVersion,
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore,
    string Explanation,
    string ComponentJson,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    DateTimeOffset CreatedAtUtc,
    string? ScoringConfigVersion = null,
    string? StrategyName = null,
    string? CollectionProvenance = null);
