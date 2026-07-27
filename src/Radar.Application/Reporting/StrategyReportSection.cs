namespace Radar.Application.Reporting;

/// <summary>
/// One configured scoring strategy's own plain ranked table (spec 150). Spec 137 made the PRIMARY strategy
/// "the series the weekly report renders", so a run with three strategies produced a report about one of
/// them; this section is how the other two stop being invisible JSON under
/// <c>data/scores/strategies/{name}/</c>.
/// <para>
/// <b>Deliberately uncombined.</b> There is no disagreement metric, no merged ranking and no composite
/// score here, and adding one is out of scope by design: a computed "these strategies disagree" number over
/// a few days of accrued history would rank noise and invite trusting it. The reader compares by eye, and
/// ranking strategies against subsequent price movement is spec 140's
/// <c>data/efficacy/strategy-leaderboard.md</c> — not this table.
/// </para>
/// <para>
/// The three counts are all rendered, because the difference between them is information rather than
/// bookkeeping: <see cref="CompaniesScored"/> is how many companies this strategy produced an in-period
/// snapshot for, <see cref="CompaniesWithLinkedEvidence"/> is how many of those had at least one
/// score-evidence link (spec 53: a score computed from zero in-window signals is an absence of data, not an
/// opportunity, so it is not surfaced), and <c>Rows.Count</c> is how many actually fit under the report's
/// <c>MaxItems</c> cap. When the cap bites, <see cref="Truncated"/> is true and the renderer says so — a
/// silently shortened table is the spec-125 failure that motivated raising that cap in the first place.
/// </para>
/// </summary>
/// <param name="StrategyName">The strategy's configured name — its stable series identity (spec 141).</param>
/// <param name="FormulaVersion">The <c>radar-formula-vN</c> this strategy scores with (spec 146).</param>
/// <param name="ScoringConfigVersion">
/// The strategy engine's <c>ScoringConfigVersion</c> fingerprint, DISPLAYED verbatim and never computed
/// here. Null when the engine reports no stamp, which the renderer prints as <c>(unstamped)</c> rather than
/// as an empty field.
/// </param>
/// <param name="IsPrimary">True for the strategy the narrative sections above describe.</param>
/// <param name="CompaniesScored">Companies with an in-period snapshot from this strategy.</param>
/// <param name="CompaniesWithLinkedEvidence">How many of those had ≥ 1 score-evidence link.</param>
/// <param name="Rows">The surfaced rows, already ranked and already capped.</param>
public sealed record StrategyReportSection(
    string StrategyName,
    string FormulaVersion,
    string? ScoringConfigVersion,
    bool IsPrimary,
    int CompaniesScored,
    int CompaniesWithLinkedEvidence,
    IReadOnlyList<StrategyReportRow> Rows)
{
    /// <summary>
    /// True when the <c>MaxItems</c> cap actually removed rows that would otherwise have surfaced — i.e.
    /// more companies had linked evidence than there are rows. Derived, so it can never disagree with the
    /// numbers next to it.
    /// </summary>
    public bool Truncated => CompaniesWithLinkedEvidence > Rows.Count;
}
