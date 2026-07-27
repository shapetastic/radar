using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// A deterministic, fully synthetic score/price world for the spec-140 harness tests.
/// <para>
/// Four companies on straight-line price paths with strictly ordered slopes, so the forward return over any
/// horizon is strictly ordered by company index (0 highest → 3 lowest, company 2 flat at exactly 0). Scores
/// are an explicit function of (company, date), so a test can dial the score↔return relationship — and its
/// SIGN — independently in the in-sample and out-of-sample halves.
/// </para>
/// <para>No clock, no randomness: every value here is a pure function of its indices.</para>
/// </summary>
internal static class ComparisonFixtures
{
    public const int HorizonDays = 21;
    public const int AsOfDateCount = 30;
    public const int InSampleDateCount = 20;      // with HoldOutFraction = 1/3 over 30 dates
    public const double HoldOutFraction = 1.0 / 3.0;

    public static readonly DateOnly FirstAsOf = new(2026, 1, 1);

    /// <summary>Fixed ids so two runs of the same fixture are byte-identical (AD-3).</summary>
    public static readonly Guid[] CompanyIds =
    [
        new("11111111-1111-1111-1111-111111111111"),
        new("22222222-2222-2222-2222-222222222222"),
        new("33333333-3333-3333-3333-333333333333"),
        new("44444444-4444-4444-4444-444444444444"),
    ];

    public static readonly string[] Tickers = ["AAAA", "BBBB", "CCCC", "DDDD"];

    /// <summary>Daily price slope per company: strictly decreasing, so forward return is ordered by index.</summary>
    private static readonly decimal[] Slopes = [0.5m, 0.25m, 0m, -0.2m];

    public static StrategyComparisonOptions Options(int minimumObservations = 20) =>
        new(HorizonDays, HoldOutFraction, minimumObservations);

    public static DateOnly AsOf(int dateIndex) => FirstAsOf.AddDays(dateIndex);

    /// <summary>Daily bars for t = 0..90 — comfortably past the last as-of date plus the horizon.</summary>
    public static IReadOnlyList<PriceBar> Bars(int companyIndex)
    {
        var bars = new List<PriceBar>(91);
        for (var t = 0; t <= 90; t++)
        {
            var price = 100m + (Slopes[companyIndex] * t);
            bars.Add(new PriceBar(
                FirstAsOf.AddDays(t),
                Open: price,
                High: price,
                Low: price,
                Close: price,
                AdjClose: price,
                Volume: 1000));
        }

        return bars;
    }

    /// <summary>
    /// One strategy over the four companies. <paramref name="score"/> receives (companyIndex, dateIndex) and
    /// returns the opportunity score; <paramref name="dateIndexes"/> selects which as-of dates it scored, and
    /// <paramref name="companyIndexes"/> which companies (both default to everything).
    /// </summary>
    public static StrategyScoreSeries Strategy(
        string name,
        Func<int, int, int> score,
        IEnumerable<int>? dateIndexes = null,
        IEnumerable<int>? companyIndexes = null)
    {
        var dates = (dateIndexes ?? Enumerable.Range(0, AsOfDateCount)).ToList();
        var companies = (companyIndexes ?? Enumerable.Range(0, CompanyIds.Length)).ToList();

        var series = new List<CompanyEfficacySeries>();
        foreach (var c in companies)
        {
            var points = new List<EfficacyPoint>();
            foreach (var d in dates)
            {
                var asOf = AsOf(d);
                points.Add(new EfficacyPoint(
                    ScoreDate: asOf,
                    TrajectoryScore: 0,
                    OpportunityScore: score(c, d),
                    AttentionScore: 0,
                    EvidenceConfidenceScore: 0,
                    SignalVelocityScore: 0,
                    SeriesKey: name,
                    ScoringConfigVersion: null,
                    PriceAsOfDate: null,
                    PriceClose: null,
                    PriceAdjClose: null)
                {
                    AsOfDate = asOf,
                });
            }

            series.Add(new CompanyEfficacySeries(
                CompanyIds[c], $"Company {Tickers[c]}", Tickers[c], points, Bars(c)));
        }

        return new StrategyScoreSeries(name, series);
    }

    /// <summary>
    /// Scores aligned with return in-sample and DELIBERATELY REVERSED out-of-sample: the strategy that wins
    /// the ranking is the one that then fails on the held-out window. The ±25 jitter exceeds the 20-point base
    /// gap, so adjacent companies cross and |ρ| never reaches 1.
    /// </summary>
    public static int AlignedThenReversed(int companyIndex, int dateIndex)
    {
        int[] alignedBase = [70, 50, 30, 10];
        int[] reversedBase = [10, 30, 50, 70];
        var baseScore = dateIndex < InSampleDateCount
            ? alignedBase[companyIndex]
            : reversedBase[companyIndex];
        return baseScore + (25 * ((dateIndex + companyIndex) % 2));
    }

    /// <summary>A score that varies only with the DATE, so it carries almost no cross-company information.</summary>
    public static int DateOnlyScore(int companyIndex, int dateIndex)
    {
        _ = companyIndex;
        return 20 + ((dateIndex % 5) * 10);
    }

    /// <summary>Scores aligned with return in BOTH halves — the strategy that holds up out-of-sample.</summary>
    public static int AlignedThroughout(int companyIndex, int dateIndex)
    {
        int[] alignedBase = [70, 50, 30, 10];
        return alignedBase[companyIndex] + (25 * ((dateIndex + companyIndex) % 2));
    }

    /// <summary>
    /// The mirror of <see cref="AlignedThenReversed"/>: nearly no in-sample signal, strong out-of-sample
    /// alignment. Pairing the two makes the ranking pick the strategy that is WORSE on the held-out window —
    /// which is only possible if the ranking genuinely never saw it.
    /// </summary>
    public static int WeakThenAligned(int companyIndex, int dateIndex) =>
        dateIndex < InSampleDateCount
            ? DateOnlyScore(companyIndex, dateIndex)
            : AlignedThroughout(companyIndex, dateIndex);
}
