using Radar.Application.Efficacy;
using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// The spec-140 multiple-comparisons discipline, asserted on the harness itself: a real chronological
/// hold-out, an out-of-sample headline, an honest N, and named pruning.
/// </summary>
public sealed class StrategyComparisonHarnessTests
{
    private static readonly StrategyComparisonHarness Harness = new();

    /// <summary>
    /// The spec-183 full-coverage benchmark: shared across the tests below, so every historical assertion in
    /// this file now runs over the EXCESS pipeline (per-date excess is a positive affine transform of raw
    /// for members, so every engineered ordering — and therefore every behavioural assertion — survives).
    /// </summary>
    private static readonly UniverseBenchmark Benchmark = ComparisonFixtures.Benchmark();

    [Fact]
    public void Compare_SplitsAsOfDatesChronologicallyIntoTwoDisjointWindows()
    {
        var leaderboard = Harness.Compare(
            [ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout)],
            ComparisonFixtures.Options(),
            Benchmark);

        var w = leaderboard.Windows;

        Assert.Equal(ComparisonFixtures.AsOfDateCount, w.TotalAsOfDates);
        Assert.Equal(ComparisonFixtures.InSampleDateCount, w.InSampleAsOfDates);
        Assert.Equal(
            ComparisonFixtures.AsOfDateCount - ComparisonFixtures.InSampleDateCount,
            w.OutOfSampleAsOfDates);

        // The two counts partition the total exactly — no date is counted twice or lost.
        Assert.Equal(w.TotalAsOfDates, w.InSampleAsOfDates + w.OutOfSampleAsOfDates);

        // …and the windows are chronologically ordered and disjoint: over a SORTED DISTINCT date list, an
        // in-sample end strictly before the out-of-sample start is exactly disjointness.
        Assert.Equal(ComparisonFixtures.AsOf(0), w.InSampleStart);
        Assert.Equal(ComparisonFixtures.AsOf(ComparisonFixtures.InSampleDateCount - 1), w.InSampleEnd);
        Assert.Equal(ComparisonFixtures.AsOf(ComparisonFixtures.InSampleDateCount), w.OutOfSampleStart);
        Assert.Equal(ComparisonFixtures.AsOf(ComparisonFixtures.AsOfDateCount - 1), w.OutOfSampleEnd);
        Assert.True(w.InSampleEnd < w.OutOfSampleStart);
    }

    [Fact]
    public void Compare_ObservationsAreCountedInExactlyOneWindow()
    {
        var leaderboard = Harness.Compare(
            [ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout)],
            ComparisonFixtures.Options(),
            Benchmark);

        var row = Assert.Single(leaderboard.Rows);

        // 4 companies × 20 in-sample dates and × 10 out-of-sample dates; every as-of date has a forward pair.
        Assert.Equal(80, row.InSample.Coverage.Observations);
        Assert.Equal(40, row.OutOfSample.Coverage.Observations);
        Assert.Equal(0, row.ObservationsWithoutForwardPrice);

        // The fixture's daily bars span every as-of date plus the horizon, so no window is partial either.
        Assert.Equal(0, row.ObservationsWithPartialWindow);

        Assert.Equal(4, row.InSample.Coverage.DistinctCompanies);
        Assert.Equal(4, row.OutOfSample.Coverage.DistinctCompanies);
        Assert.Equal(20, row.InSample.Coverage.DistinctAsOfDates);
        Assert.Equal(10, row.OutOfSample.Coverage.DistinctAsOfDates);
    }

    [Fact]
    public void Compare_RanksOnInSampleAndReportsTheOutOfSampleHeadline()
    {
        // 'overfit' looks strong in-sample and INVERTS on the held-out window; 'late-bloomer' is the exact
        // mirror. If the ranking leaked the out-of-sample data, 'late-bloomer' would be rank 1. It is not —
        // the harness deliberately crowns the strategy that then performs worse on the data it never saw,
        // and reports that worse number as the headline.
        var leaderboard = Harness.Compare(
            [
                ComparisonFixtures.Strategy("overfit", ComparisonFixtures.AlignedThenReversed),
                ComparisonFixtures.Strategy("late-bloomer", ComparisonFixtures.WeakThenAligned),
            ],
            ComparisonFixtures.Options(),
            Benchmark);

        Assert.Equal(2, leaderboard.StrategiesCompared);
        Assert.Empty(leaderboard.DroppedStrategies);
        Assert.Equal(2, leaderboard.Rows.Count);

        var first = leaderboard.Rows[0];
        var second = leaderboard.Rows[1];
        Assert.Equal(1, first.Rank);
        Assert.Equal(2, second.Rank);
        Assert.Same(first, leaderboard.Headline);

        Assert.Equal("overfit", first.StrategyName);
        Assert.Equal("late-bloomer", second.StrategyName);
        Assert.True(
            first.InSample.Correlation.Rho > second.InSample.Correlation.Rho,
            "Rows must be ordered by the IN-SAMPLE metric.");

        // The engineered sign flip, and the proof the headline is not the ranking number.
        Assert.True(first.InSample.Correlation.Rho > 0.3, $"in-sample rho was {first.InSample.Correlation.Rho}");
        Assert.True(first.OutOfSample.Correlation.Rho < -0.3, $"out-of-sample rho was {first.OutOfSample.Correlation.Rho}");
        Assert.True(
            second.OutOfSample.Correlation.Rho > first.OutOfSample.Correlation.Rho,
            "The rank-1 strategy is NOT the out-of-sample winner — which is exactly what a hold-out is for.");
    }

    [Fact]
    public void Compare_TwoDifferentScoreSeriesProduceDifferentEfficacyNumbers()
    {
        var leaderboard = Harness.Compare(
            [
                ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
                ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
            ],
            ComparisonFixtures.Options(),
            Benchmark);

        var aligned = leaderboard.Rows.Single(r => r.StrategyName == "aligned");
        var dateOnly = leaderboard.Rows.Single(r => r.StrategyName == "date-only");

        Assert.NotEqual(
            aligned.InSample.Correlation.Rho, dateOnly.InSample.Correlation.Rho, 12);
        Assert.True(
            aligned.InSample.Correlation.Rho > dateOnly.InSample.Correlation.Rho,
            "A strategy whose scores track the return must out-score one whose scores ignore the company.");

        // Same companies, same prices, same dates — only the SCORES differ, and that alone moves the metric.
        Assert.Equal(
            aligned.InSample.Coverage.Observations, dateOnly.InSample.Coverage.Observations);
    }

    [Fact]
    public void Compare_ReportsHonestNAndNamesEveryDroppedStrategy()
    {
        var leaderboard = Harness.Compare(
            [
                ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
                ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),

                // Two dates, one company ⇒ 2 in-sample observations, 0 out-of-sample.
                ComparisonFixtures.Strategy(
                    "thin-in-sample",
                    ComparisonFixtures.AlignedThroughout,
                    dateIndexes: [0, 1],
                    companyIndexes: [0]),

                // Every in-sample date, but only ONE out-of-sample date ⇒ 4 held-out observations.
                ComparisonFixtures.Strategy(
                    "thin-out-of-sample",
                    ComparisonFixtures.AlignedThroughout,
                    dateIndexes: [.. Enumerable.Range(0, ComparisonFixtures.InSampleDateCount), 25]),
            ],
            ComparisonFixtures.Options(),
            Benchmark);

        Assert.Equal(4, leaderboard.StrategiesConsidered);
        Assert.Equal(2, leaderboard.StrategiesCompared);
        Assert.Equal(2, leaderboard.Rows.Count);
        Assert.Equal(2, leaderboard.DroppedStrategies.Count);

        var thinIn = leaderboard.DroppedStrategies.Single(d => d.StrategyName == "thin-in-sample");
        Assert.Equal(StrategyDropReason.InsufficientInSampleObservations, thinIn.Reason);
        Assert.Equal(2, thinIn.InSampleObservations);
        Assert.Equal(0, thinIn.OutOfSampleObservations);

        var thinOut = leaderboard.DroppedStrategies.Single(d => d.StrategyName == "thin-out-of-sample");
        Assert.Equal(StrategyDropReason.InsufficientOutOfSampleObservations, thinOut.Reason);
        Assert.Equal(80, thinOut.InSampleObservations);
        Assert.Equal(4, thinOut.OutOfSampleObservations);

        // A dropped strategy is never silently folded into the ranking.
        Assert.DoesNotContain(leaderboard.Rows, r => r.StrategyName == "thin-in-sample");
        Assert.DoesNotContain(leaderboard.Rows, r => r.StrategyName == "thin-out-of-sample");
    }

    [Fact]
    public void Compare_DropsADegenerateSeriesWithItsMetricReason()
    {
        var leaderboard = Harness.Compare(
            [
                ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
                ComparisonFixtures.Strategy("flat", static (_, _) => 50),
            ],
            ComparisonFixtures.Options(),
            Benchmark);

        var flat = Assert.Single(leaderboard.DroppedStrategies);
        Assert.Equal("flat", flat.StrategyName);
        Assert.Equal(StrategyDropReason.DegenerateInSampleMetric, flat.Reason);
        Assert.Equal(RankCorrelationUndefinedReason.ConstantScores, flat.MetricReason);
        Assert.Equal(1, leaderboard.StrategiesCompared);
    }

    [Fact]
    public void Compare_CountsObservationsWithNoForwardPriceRatherThanHidingThem()
    {
        // One company loses its price bars entirely, so every one of its scored days becomes unusable. No
        // duplicate points here — this isolates "counted, not silently dropped" from the de-duping below.
        var full = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var withoutPrices = new StrategyScoreSeries(
            full.StrategyName,
            [.. full.Companies.Select((c, i) =>
                i == 0
                    ? new CompanyEfficacySeries(
                        c.CompanyId, c.CompanyName, c.Ticker, c.Points, [])
                    : c)]);

        var leaderboard = Harness.Compare([withoutPrices], ComparisonFixtures.Options(), Benchmark);

        var row = Assert.Single(leaderboard.Rows);
        Assert.Equal(ComparisonFixtures.AsOfDateCount, row.ObservationsWithoutForwardPrice);
        Assert.Equal(60, row.InSample.Coverage.Observations);   // 3 remaining companies × 20 dates
        Assert.Equal(3, row.InSample.Coverage.DistinctCompanies);
    }

    [Fact]
    public void Compare_CountsCompanyDaysWithoutAForwardPriceOnceEvenWhenTheDayWasScoredTwice()
    {
        // Two runs on the same day both stamp a snapshot, so every (company, as-of) pair appears twice. The
        // usable side collapses those to one observation; the unusable side must collapse them on the SAME
        // key, or "observations without a forward price" is measured in a different unit from the coverage
        // counts printed beside it and reads as double the coverage actually lost.
        var full = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var duplicatedAndPartlyPriceless = new StrategyScoreSeries(
            full.StrategyName,
            [.. full.Companies.Select((c, i) => new CompanyEfficacySeries(
                c.CompanyId,
                c.CompanyName,
                c.Ticker,
                [.. c.Points, .. c.Points],           // same company-days, scored twice
                i == 0 ? [] : c.PriceBars))]);        // company 0 has no price at all

        var leaderboard = Harness.Compare([duplicatedAndPartlyPriceless], ComparisonFixtures.Options(), Benchmark);

        var row = Assert.Single(leaderboard.Rows);

        // 30 company-days lost, not 60 points.
        Assert.Equal(ComparisonFixtures.AsOfDateCount, row.ObservationsWithoutForwardPrice);

        // …and the usable side is unmoved by the duplication, which is what makes the two commensurable.
        Assert.Equal(60, row.InSample.Coverage.Observations);
        Assert.Equal(30, row.OutOfSample.Coverage.Observations);
        Assert.Equal(3, row.InSample.Coverage.DistinctCompanies);
    }

    [Fact]
    public void Compare_DoesNotCountACompanyDayAsLostWhenAnotherSeriesForTheSameCompanyPricedIt()
    {
        // A strategy may legally carry one company id in two series — and only then can two occurrences of the
        // same (company, as-of) key disagree about whether a forward return exists, because the bars differ.
        // A key some occurrence DID price is not lost coverage, so it must not be counted as lost.
        var full = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var company0 = full.Companies[0];
        var withAPricelessShadow = new StrategyScoreSeries(
            full.StrategyName,
            [
                .. full.Companies,
                new CompanyEfficacySeries(
                    company0.CompanyId, company0.CompanyName, company0.Ticker, company0.Points, []),
            ]);

        var leaderboard = Harness.Compare([withAPricelessShadow], ComparisonFixtures.Options(), Benchmark);

        var row = Assert.Single(leaderboard.Rows);
        Assert.Equal(0, row.ObservationsWithoutForwardPrice);

        // Every company-day is still scored exactly once — the shadow series adds no observation either.
        Assert.Equal(80, row.InSample.Coverage.Observations);
        Assert.Equal(4, row.InSample.Coverage.DistinctCompanies);
    }

    /// <summary>
    /// Drops every bar after <paramref name="lastBarDayIndex"/>, so an as-of date late enough that
    /// <c>D + horizon</c> lands past the truncation gets a forward window that stops short of the horizon —
    /// exactly the shape spec 152 must classify as <c>PartialWindow</c> instead of a full-horizon return.
    /// </summary>
    private static StrategyScoreSeries TruncateBarsAfter(StrategyScoreSeries strategy, int lastBarDayIndex)
    {
        var lastBar = ComparisonFixtures.AsOf(lastBarDayIndex);
        return new StrategyScoreSeries(
            strategy.StrategyName,
            [.. strategy.Companies.Select(c => new CompanyEfficacySeries(
                c.CompanyId,
                c.CompanyName,
                c.Ticker,
                c.Points,
                [.. c.PriceBars.Where(b => b.Date <= lastBar)]))]);
    }

    [Fact]
    public void Compare_CountsAPartialForwardWindowSeparatelyAndKeepsItOutOfTheCorrelation()
    {
        // Bars stop at day 40, so with a 21-day horizon the as-of dates from day 24 on have a window that
        // reaches only day 40 — a shortfall of 5+ days against a tolerance of 4. Before spec 152 those were
        // computed as 16-to-20-day returns and reported as 21-day ones.
        var partiallyPriced = TruncateBarsAfter(
            ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout), lastBarDayIndex: 40);

        var leaderboard = Harness.Compare([partiallyPriced], ComparisonFixtures.Options(), Benchmark);
        var row = Assert.Single(leaderboard.Rows);

        // Days 24..29 × 4 companies = 24 company-days short of the horizon…
        Assert.Equal(24, row.ObservationsWithPartialWindow);

        // …and NOT counted as missing price: that column keeps its exact pre-152 definition (no bar at all, a
        // single bar, or a non-positive entry price). "No price" and "not the horizon" are different facts.
        Assert.Equal(0, row.ObservationsWithoutForwardPrice);

        // Excluded from the metric, not relabelled: days 0..23 are the only usable dates, split 16 / 8.
        Assert.Equal(64, row.InSample.Coverage.Observations);
        Assert.Equal(32, row.OutOfSample.Coverage.Observations);
        Assert.Equal(16, row.InSample.Coverage.DistinctAsOfDates);
        Assert.Equal(8, row.OutOfSample.Coverage.DistinctAsOfDates);
        Assert.Equal(24, leaderboard.Windows.TotalAsOfDates);

        // The proof it is EXCLUDED rather than merely counted: the same series scored only over the dates whose
        // windows are complete produces the identical metric, differing solely in the partial tally.
        var completeDatesOnly = TruncateBarsAfter(
            ComparisonFixtures.Strategy(
                "aligned", ComparisonFixtures.AlignedThroughout, dateIndexes: Enumerable.Range(0, 24)),
            lastBarDayIndex: 40);
        var withoutThePartials = Assert.Single(
            Harness.Compare([completeDatesOnly], ComparisonFixtures.Options(), Benchmark).Rows);

        Assert.Equal(0, withoutThePartials.ObservationsWithPartialWindow);
        Assert.Equal(row.InSample.Correlation.Rho, withoutThePartials.InSample.Correlation.Rho, 12);
        Assert.Equal(row.OutOfSample.Correlation.Rho, withoutThePartials.OutOfSample.Correlation.Rho, 12);
        Assert.Equal(row.InSample.Coverage, withoutThePartials.InSample.Coverage);
        Assert.Equal(row.OutOfSample.Coverage, withoutThePartials.OutOfSample.Coverage);
    }

    [Fact]
    public void Compare_KeepsTheMissingPriceAndPartialWindowTalliesSeparate()
    {
        // All three outcomes at once: company 0 has no price at all (missing), companies 1-3 have price that
        // stops at day 40 (usable up to day 23, partial from day 24). Two counts, two meanings, no overlap.
        var full = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var truncated = TruncateBarsAfter(full, lastBarDayIndex: 40);
        var mixed = new StrategyScoreSeries(
            truncated.StrategyName,
            [.. truncated.Companies.Select((c, i) => i == 0
                ? new CompanyEfficacySeries(c.CompanyId, c.CompanyName, c.Ticker, c.Points, [])
                : c)]);

        var row = Assert.Single(Harness.Compare([mixed], ComparisonFixtures.Options(), Benchmark).Rows);

        Assert.Equal(ComparisonFixtures.AsOfDateCount, row.ObservationsWithoutForwardPrice);   // 30 company-days
        Assert.Equal(18, row.ObservationsWithPartialWindow);                                   // 6 dates × 3
        Assert.Equal(48, row.InSample.Coverage.Observations);                                   // 16 dates × 3
        Assert.Equal(24, row.OutOfSample.Coverage.Observations);                                // 8 dates × 3
        Assert.Equal(3, row.InSample.Coverage.DistinctCompanies);
    }

    [Fact]
    public void Compare_DoesNotCountACompanyDayAsPartialWhenAnotherSeriesCoveredTheWholeHorizon()
    {
        // The same de-dupe rule the missing-price tally uses, for the same reason: a (company, as-of) key that
        // some occurrence DID cover to the horizon is not lost coverage, so it must not be reported as partial.
        // A strategy carrying one company id in two series with different bars is the only way the two
        // occurrences of one key can disagree — and it is legal.
        var full = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var company0 = full.Companies[0];
        var truncatedShadow = new CompanyEfficacySeries(
            company0.CompanyId,
            company0.CompanyName,
            company0.Ticker,
            company0.Points,
            [.. company0.PriceBars.Where(b => b.Date <= ComparisonFixtures.AsOf(40))]);

        var withATruncatedShadow = new StrategyScoreSeries(
            full.StrategyName, [.. full.Companies, truncatedShadow]);

        var row = Assert.Single(Harness.Compare([withATruncatedShadow], ComparisonFixtures.Options(), Benchmark).Rows);

        Assert.Equal(0, row.ObservationsWithPartialWindow);
        Assert.Equal(0, row.ObservationsWithoutForwardPrice);

        // …and the shadow adds no observation either — every company-day is still scored exactly once.
        Assert.Equal(80, row.InSample.Coverage.Observations);
        Assert.Equal(4, row.InSample.Coverage.DistinctCompanies);
    }

    [Fact]
    public void Compare_WithNoHistoryProducesAnHonestEmptyLeaderboardRatherThanThrowing()
    {
        var leaderboard = Harness.Compare([], ComparisonFixtures.Options(), Benchmark);

        Assert.Equal(0, leaderboard.StrategiesCompared);
        Assert.Equal(0, leaderboard.StrategiesConsidered);
        Assert.Empty(leaderboard.Rows);
        Assert.Empty(leaderboard.DroppedStrategies);
        Assert.Null(leaderboard.Headline);
        Assert.Equal(0, leaderboard.Windows.TotalAsOfDates);
        Assert.Null(leaderboard.Windows.InSampleStart);
    }

    [Fact]
    public void Compare_IsDeterministic_SameInputsYieldAnIdenticalLeaderboard()
    {
        StrategyLeaderboard Run() => Harness.Compare(
            [
                ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
                ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
                ComparisonFixtures.Strategy("overfit", ComparisonFixtures.AlignedThenReversed),
            ],
            ComparisonFixtures.Options(),
            Benchmark);

        var first = Run();
        var second = Run();

        Assert.Equal(first.StrategiesCompared, second.StrategiesCompared);
        Assert.Equal(
            first.Rows.Select(r => (r.Rank, r.StrategyName, r.InSample.Correlation.Rho, r.OutOfSample.Correlation.Rho)),
            second.Rows.Select(r => (r.Rank, r.StrategyName, r.InSample.Correlation.Rho, r.OutOfSample.Correlation.Rho)));

        var renderer = new StrategyLeaderboardRenderer();
        Assert.Equal(renderer.RenderCsv(first), renderer.RenderCsv(second));
        Assert.Equal(renderer.RenderMarkdown(first), renderer.RenderMarkdown(second));
    }

    [Fact]
    public void Compare_IsIndependentOfTheOrderStrategiesAreSuppliedIn()
    {
        var renderer = new StrategyLeaderboardRenderer();

        var forward = Harness.Compare(
            [
                ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
                ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
            ],
            ComparisonFixtures.Options(),
            Benchmark);

        var reversed = Harness.Compare(
            [
                ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
                ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
            ],
            ComparisonFixtures.Options(),
            Benchmark);

        Assert.Equal(renderer.RenderCsv(forward), renderer.RenderCsv(reversed));
    }

    [Fact]
    public void Compare_UsesTheAsOfDateNotTheRunDate()
    {
        // Two identical strategies whose points differ ONLY in ScoreDate (the run instant). The as-of date is
        // what bounds the forward window, so the metrics must be identical — this is what keeps a spec-139
        // replay series, whose CreatedAtUtc is the replay's wall clock, honest.
        var byAsOf = ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout);
        var shiftedRunDate = new StrategyScoreSeries(
            byAsOf.StrategyName,
            [.. byAsOf.Companies.Select(c => new CompanyEfficacySeries(
                c.CompanyId,
                c.CompanyName,
                c.Ticker,
                [.. c.Points.Select(p => p with { ScoreDate = new DateOnly(2030, 12, 31) })],
                c.PriceBars))]);

        var a = Harness.Compare([byAsOf], ComparisonFixtures.Options(), Benchmark);
        var b = Harness.Compare([shiftedRunDate], ComparisonFixtures.Options(), Benchmark);

        Assert.Equal(a.Rows[0].InSample.Correlation.Rho, b.Rows[0].InSample.Correlation.Rho, 12);
        Assert.Equal(a.Windows, b.Windows);
    }
}
