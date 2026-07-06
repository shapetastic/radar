using Radar.Application.Prices;

namespace Radar.Application.Efficacy;

/// <summary>
/// A company's efficacy series: sparse score points (segment-keyed by ScoringConfigVersion) overlaid on the
/// dense daily price bars, for a per-company score-vs-price visual (AD-14 read side).
/// </summary>
public sealed record CompanyEfficacySeries(
    Guid CompanyId,
    string CompanyName,
    string Ticker,
    IReadOnlyList<EfficacyPoint> Points,        // ascending by ScoreDate
    IReadOnlyList<PriceBar> PriceBars);         // ascending by Date (the dense line)
