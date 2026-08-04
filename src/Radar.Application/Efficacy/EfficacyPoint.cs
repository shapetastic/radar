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
    decimal? PriceAdjClose)
{
    /// <summary>
    /// The date the score's knowledge window ENDED (the snapshot's <c>WindowEndUtc</c>) — the <c>D</c> in
    /// spec 140's "score at D judged against price over (D, D+h]". Additive and trailing (init-only) so every
    /// existing construction site and every existing rendered artifact is unchanged; the CSV/SVG renderers do
    /// not read it, so their output is byte-identical.
    /// <para>
    /// It is NOT the same fact as <see cref="ScoreDate"/> (which is <c>CreatedAtUtc</c>, the run instant). For
    /// a forward run the two coincide. For a spec-139 <b>replay</b> snapshot they do not: <c>CreatedAtUtc</c>
    /// is the replay process's wall clock (identical for every point in the replay), while
    /// <c>WindowEndUtc</c> is the simulated as-of instant that actually bounded what the score could see. Only
    /// the as-of date is a meaningful anchor for a forward-return horizon, so the comparison harness uses it.
    /// </para>
    /// <para>
    /// <c>null</c> means "not recorded" (a hand-constructed point); consumers fall back to
    /// <see cref="ScoreDate"/>.
    /// </para>
    /// </summary>
    public DateOnly? AsOfDate { get; init; }

    /// <summary>
    /// The EXACT instant the score's knowledge window ended (the snapshot's <c>WindowEndUtc</c>, spec 170) —
    /// the same fact as <see cref="AsOfDate"/> at full precision. Additive and trailing (init-only), so every
    /// existing construction site compiles unchanged; the per-company CSV/SVG renderers and the marginal
    /// leaderboard do not read it, so their output stays byte-identical (asserted, not assumed).
    /// <para>
    /// It exists because spec 155's paired comparison must intersect arms on the exact scoring instant, not
    /// the calendar date: after a partial rerun, two arms' same-day snapshots can represent DIFFERENT
    /// knowledge cutoffs, and pairing them by date would attribute to strategy difference what is actually a
    /// difference in what each arm could see. <c>null</c> means "not recorded" (a hand-constructed point) —
    /// such a point FAILS CLOSED out of the claim path (counted, never date-paired as a fallback).
    /// </para>
    /// </summary>
    public DateTimeOffset? AsOfInstantUtc { get; init; }
}
