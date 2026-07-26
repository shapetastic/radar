namespace Radar.Application.Efficacy;

/// <summary>
/// One joined efficacy point: a score snapshot's numeric components paired (no look-ahead) to the price bar
/// at-or-before its date. VALIDATION/RESEARCH data only (AD-14) — never a scoring input.
/// <para>
/// It carries BOTH identity facts, because since spec 141 they answer different questions:
/// <c>SeriesKey</c> (the snapshot's strategy name, blank/null canonicalised to <c>"default"</c> — see
/// <c>ScoreSeriesKey</c>) is what SEGMENTS the series, while <c>ScoringConfigVersion</c> is retained
/// provenance/annotation — still rendered as a config-change boundary tick, but no longer a segment key.
/// </para>
/// </summary>
public sealed record EfficacyPoint(
    DateOnly ScoreDate,
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore,
    string SeriesKey,               // the strategy-name series key (never blank; legacy null ⇒ "default")
    string? ScoringConfigVersion,   // the config fingerprint (null = pre-stamp/unknown); annotation, not a key
    DateOnly? PriceAsOfDate,        // the actual bar date used (at-or-before ScoreDate), null if unpaired
    decimal? PriceClose,
    decimal? PriceAdjClose);
