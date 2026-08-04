using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;
using Radar.Application.Prices;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// A deterministic synthetic world for the spec-155 paired-harness tests, built for CONTROLLED per-date rhos:
/// six companies on straight-line price paths whose slopes make the forward return strictly ordered by index
/// for companies 0..3 (0 highest → 3 lowest, negative) and EXACTLY zero for companies 4 and 5 (flat price) —
/// so a joint cross-section of {4, 5} is a genuinely constant outcome.
/// <para>
/// Scores are an explicit <c>(companyIndex, dayOffset) → int?</c> function (null = that company-date has no
/// point), so a test can dial each arm's per-date rho — and its membership — independently. No clock, no
/// randomness (AD-3).
/// </para>
/// </summary>
internal static class PairedFixtures
{
    public const int HorizonDays = 21;
    public const int ExitToleranceDays = 4;

    public static readonly DateOnly FirstAsOf = new(2026, 1, 1);

    public static readonly Guid[] CompanyIds =
    [
        new("aaaaaaaa-1111-1111-1111-111111111111"),
        new("aaaaaaaa-2222-2222-2222-222222222222"),
        new("aaaaaaaa-3333-3333-3333-333333333333"),
        new("aaaaaaaa-4444-4444-4444-444444444444"),
        new("aaaaaaaa-5555-5555-5555-555555555555"),
        new("aaaaaaaa-6666-6666-6666-666666666666"),
    ];

    public static readonly string[] Tickers = ["PAAA", "PBBB", "PCCC", "PDDD", "PEEE", "PFFF"];

    private static readonly decimal[] Slopes = [0.5m, 0.25m, 0.1m, -0.2m, 0m, 0m];

    /// <summary>The shared spec-140 knobs the paired options reuse (hold-out/minimum are unused here).</summary>
    public static StrategyComparisonOptions Comparison { get; } =
        new(HorizonDays, 0.30, 20, ExitToleranceDays);

    public static PairedComparisonOptions Options(
        int minimumCompaniesPerDate = 2,
        DateOnly? firstEligibleAsOf = null,
        string configuredPrimary = "primary") =>
        new(configuredPrimary, firstEligibleAsOf, minimumCompaniesPerDate, Comparison);

    public static DateOnly AsOf(int dayOffset) => FirstAsOf.AddDays(dayOffset);

    /// <summary>
    /// The default exact scoring instant for a day offset: midnight UTC of the as-of date, so every arm
    /// shares one instant per day (the normal full-run shape) and the spec-170 exact-instant intersection
    /// reproduces the date intersection. Tests dial per-arm instants via <c>Series</c>'s <c>instant</c>
    /// function to manufacture partial-rerun mismatches.
    /// </summary>
    public static DateTimeOffset InstantOf(int dayOffset) =>
        new(AsOf(dayOffset).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>Day offsets spaced exactly one horizon apart — every one survives the purge.</summary>
    public static IReadOnlyList<int> Spaced(int count) =>
        [.. Enumerable.Range(0, count).Select(i => i * HorizonDays)];

    /// <summary>Dense daily day offsets 0..count−1 — the purge keeps only every 21st.</summary>
    public static IReadOnlyList<int> Daily(int count) => [.. Enumerable.Range(0, count)];

    /// <summary>
    /// Daily bars for t = 0..320 (comfortably past every fixture as-of date plus the horizon). With
    /// <paramref name="weekdaysOnly"/> the series skips Saturdays/Sundays, giving realistic entry/exit gaps
    /// that the 4-day exit tolerance absorbs.
    /// </summary>
    public static IReadOnlyList<PriceBar> Bars(
        int companyIndex, bool weekdaysOnly = false, decimal? slopeOverride = null)
    {
        var slope = slopeOverride ?? Slopes[companyIndex];
        var bars = new List<PriceBar>(321);
        for (var t = 0; t <= 320; t++)
        {
            var date = FirstAsOf.AddDays(t);
            if (weekdaysOnly && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var price = 100m + (slope * t);
            bars.Add(new PriceBar(
                date, Open: price, High: price, Low: price, Close: price, AdjClose: price, Volume: 1000));
        }

        return bars;
    }

    /// <summary>
    /// One strategy over the given companies and day offsets. <paramref name="score"/> returns null to omit
    /// that company-date entirely (membership control); <paramref name="barsOverride"/> lets a test hand one
    /// arm DIFFERENT price bars to manufacture an inconsistent outcome; <paramref name="instant"/> lets a
    /// test dial the exact scoring instant per (company, day) — returning null omits the instant entirely
    /// (the legacy-point shape) — and defaults to the shared midnight <see cref="InstantOf"/>.
    /// </summary>
    public static StrategyScoreSeries Series(
        string name,
        Func<int, int, int?> score,
        IEnumerable<int> dayOffsets,
        IEnumerable<int>? companyIndexes = null,
        bool weekdaysOnly = false,
        Func<int, IReadOnlyList<PriceBar>>? barsOverride = null,
        Func<int, int, DateTimeOffset?>? instant = null)
    {
        var dates = dayOffsets.ToList();
        var companies = (companyIndexes ?? Enumerable.Range(0, 4)).ToList();
        instant ??= (_, d) => InstantOf(d);

        var series = new List<CompanyEfficacySeries>();
        foreach (var c in companies)
        {
            var points = new List<EfficacyPoint>();
            foreach (var d in dates)
            {
                if (score(c, d) is not { } value)
                {
                    continue;
                }

                var asOf = AsOf(d);
                points.Add(new EfficacyPoint(
                    ScoreDate: asOf,
                    TrajectoryScore: 0,
                    OpportunityScore: value,
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
                    AsOfInstantUtc = instant(c, d),
                });
            }

            series.Add(new CompanyEfficacySeries(
                CompanyIds[c],
                $"Company {Tickers[c]}",
                Tickers[c],
                points,
                barsOverride?.Invoke(c) ?? Bars(c, weekdaysOnly)));
        }

        return new StrategyScoreSeries(name, series);
    }

    /// <summary>Decreasing in company index — perfectly aligned with the outcome ordering ⇒ per-date ρ = +1.</summary>
    public static int? Aligned(int companyIndex, int dayOffset) => 80 - (10 * companyIndex);

    /// <summary>Increasing in company index — perfectly anti-aligned ⇒ per-date ρ = −1.</summary>
    public static int? AntiAligned(int companyIndex, int dayOffset) => 20 + (10 * companyIndex);
}
