namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// One strategy's whole score history, as the deterministic per-company join already produces it (spec 140
/// input). The comparison harness consumes ONLY this — scoring OUTPUT that has already been persisted, plus
/// the price reference series riding along on each company — so there is no path from the comparison back into
/// anything that computes a score (AD-14).
/// </summary>
/// <param name="StrategyName">The strategy's name — its series identity (see <c>ScoreSeriesKey</c>).</param>
/// <param name="Companies">The per-company joined series, one entry per seeded company with a ticker.</param>
public sealed record StrategyScoreSeries(
    string StrategyName,
    IReadOnlyList<CompanyEfficacySeries> Companies);
