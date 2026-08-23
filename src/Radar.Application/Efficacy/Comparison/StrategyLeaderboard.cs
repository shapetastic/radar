namespace Radar.Application.Efficacy.Comparison;

/// <summary>Why a strategy was excluded from the ranking. Machine-readable — never a free-text-only reason.</summary>
public enum StrategyDropReason
{
    /// <summary>Fewer usable observations than <c>MinimumObservations</c> in the in-sample window.</summary>
    InsufficientInSampleObservations,

    /// <summary>Fewer usable observations than <c>MinimumObservations</c> in the out-of-sample window.</summary>
    InsufficientOutOfSampleObservations,

    /// <summary>The in-sample rank correlation is undefined (see the accompanying metric reason).</summary>
    DegenerateInSampleMetric,

    /// <summary>The out-of-sample rank correlation is undefined (see the accompanying metric reason).</summary>
    DegenerateOutOfSampleMetric,
}

/// <summary>
/// A strategy that was NOT ranked, with the machine-readable reason and the counts that triggered it. Carried
/// as a FIELD on the leaderboard rather than only logged: spec 140's "no silent strategy pruning" — a quietly
/// dropped loser inflates the apparent skill of everything that survived, so the drop travels with the result.
/// </summary>
public sealed record DroppedStrategy(
    string StrategyName,
    StrategyDropReason Reason,
    int InSampleObservations,
    int OutOfSampleObservations,
    RankCorrelationUndefinedReason MetricReason);

/// <summary>
/// How much a strategy actually scored inside one window. Surfaced because "3 companies on 2 dates" and
/// "43 companies on 60 dates" are not comparable evidence even when their ρ happens to match.
/// </summary>
public sealed record StrategyWindowCoverage(
    int Observations,
    int DistinctCompanies,
    int DistinctAsOfDates);

/// <summary>One window's metric plus the coverage it was computed over.</summary>
public sealed record StrategyWindowMetric(
    RankCorrelationResult Correlation,
    StrategyWindowCoverage Coverage);

/// <summary>
/// One ranked strategy. <see cref="Rank"/> is assigned from the IN-SAMPLE metric only; the headline number a
/// reader should weigh is <see cref="OutOfSample"/>, computed on dates the ranking never saw.
/// <para>
/// <see cref="ObservationsWithoutForwardPrice"/> counts company-days — the same (company, as-of) unit an
/// observation is, de-duped the same way — so it is directly comparable with the coverage counts beside it
/// rather than inflated by same-day re-runs that the usable side would have collapsed.
/// </para>
/// <para>
/// <see cref="ObservationsWithPartialWindow"/> is counted on that same company-day unit and is DISTINCT from
/// <see cref="ObservationsWithoutForwardPrice"/> (spec 152): "no price at all" and "some price but not the
/// horizon you asked for" are different facts about the data and must never be conflated. Conflating them was
/// the defect — a partial window used to be counted as a success, so neither column could reveal it.
/// </para>
/// <para>
/// Spec 183 adds two more counts on the same unit, again distinct facts: raw-usable observations whose EXCESS
/// return does not exist because the benchmark's coverage rule failed at that date
/// (<see cref="ObservationsBenchmarkUnavailable"/>) versus because the company is not a member of the frozen
/// universe at all (<see cref="ObservationsNotInBenchmarkUniverse"/>). Both are exclusions from the pooled
/// excess correlation, named and counted — never a silent fallback to the raw return.
/// </para>
/// </summary>
public sealed record StrategyLeaderboardRow(
    int Rank,
    string StrategyName,
    StrategyWindowMetric InSample,
    StrategyWindowMetric OutOfSample,
    int ObservationsWithoutForwardPrice,
    int ObservationsWithPartialWindow,
    int ObservationsBenchmarkUnavailable,
    int ObservationsNotInBenchmarkUniverse);

/// <summary>One unresolved benchmark member on one as-of date, with its spec-152 reason.</summary>
public sealed record BenchmarkMemberExclusion(string Ticker, ForwardReturnUnavailableReason Reason);

/// <summary>
/// One as-of date's benchmark coverage against the FROZEN pond: unresolved members stay in the denominator
/// and are listed with their reasons (spec 183 §2 — coverage is measured against the frozen universe, never
/// against whatever happened to resolve).
/// </summary>
public sealed record BenchmarkDayCoverage(
    DateOnly AsOf,
    int MemberCount,
    int ResolvedMembers,
    IReadOnlyList<BenchmarkMemberExclusion> UnresolvedMembers);

/// <summary>
/// The benchmark provenance the rendered leaderboard carries (spec 183 §2): which frozen universe (version +
/// content hash + freeze instant + member count), the per-day coverage over every as-of date the comparison
/// touched, and how many of those dates PRECEDE the freeze — those excess results are retrospective (the
/// members were selected after the fact and their prices backfilled) and every artifact labels them so.
/// </summary>
public sealed record LeaderboardBenchmarkProvenance(
    string UniverseVersion,
    string ContentHash,
    DateTimeOffset FrozenAtUtc,
    int MemberCount,
    IReadOnlyList<BenchmarkDayCoverage> Days,
    int PreFreezeAsOfDates);

/// <summary>
/// The chronological hold-out split, reported so a reader can verify it rather than trust it. The two date
/// counts partition <see cref="TotalAsOfDates"/> exactly — the split is an INDEX partition of the sorted
/// distinct as-of dates, so a date belongs to exactly one side by construction.
/// </summary>
public sealed record StrategyComparisonWindows(
    int TotalAsOfDates,
    int InSampleAsOfDates,
    int OutOfSampleAsOfDates,
    DateOnly? InSampleStart,
    DateOnly? InSampleEnd,
    DateOnly? OutOfSampleStart,
    DateOnly? OutOfSampleEnd);

/// <summary>
/// The result of spec 140's strategy-vs-price comparison: which strategies' scores tracked SUBSEQUENT price
/// movement more closely, ranked in-sample and reported out-of-sample.
/// <para>
/// This is a research statistic about Radar's own scoring, never a recommendation about a company and never
/// advice (AD-9). It ranks; it does not act — promoting a strategy is a human decision.
/// </para>
/// </summary>
/// <param name="StrategiesCompared">
/// The honest N: how many strategies were actually RANKED, i.e. the size of the set the top row was chosen
/// from. A winner picked from 20 needs a far stronger effect than one picked from 2.
/// </param>
/// <param name="StrategiesConsidered">Ranked + dropped — every strategy the harness was handed.</param>
/// <param name="Rows">Ranked best-first by the IN-SAMPLE metric; never re-ordered by the out-of-sample one.</param>
/// <param name="DroppedStrategies">Every excluded strategy, named, with its reason and counts.</param>
/// <param name="Windows">The chronological split the metrics were computed over.</param>
/// <param name="Options">The resolved knobs, so a rendered leaderboard is self-describing.</param>
/// <param name="Benchmark">
/// The spec-183 benchmark provenance, or <c>null</c> when the frozen universe could not be loaded — in which
/// case every raw-usable observation was excluded as <c>BenchmarkUnavailable</c> and the rendered artifact
/// says so explicitly (never a silent fallback to raw returns).
/// </param>
public sealed record StrategyLeaderboard(
    int StrategiesCompared,
    int StrategiesConsidered,
    IReadOnlyList<StrategyLeaderboardRow> Rows,
    IReadOnlyList<DroppedStrategy> DroppedStrategies,
    StrategyComparisonWindows Windows,
    StrategyComparisonOptions Options,
    LeaderboardBenchmarkProvenance? Benchmark = null)
{
    /// <summary>
    /// The top-ranked strategy, or <c>null</c> when nothing could be ranked. Its
    /// <see cref="StrategyLeaderboardRow.OutOfSample"/> metric is the headline number.
    /// </summary>
    public StrategyLeaderboardRow? Headline => Rows.Count > 0 ? Rows[0] : null;
}
