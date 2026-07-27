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

    [Fact]
    public void Compare_SplitsAsOfDatesChronologicallyIntoTwoDisjointWindows()
    {
        var leaderboard = Harness.Compare(
            [ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout)],
            ComparisonFixtures.Options());

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
            ComparisonFixtures.Options());

        var row = Assert.Single(leaderboard.Rows);

        // 4 companies × 20 in-sample dates and × 10 out-of-sample dates; every as-of date has a forward pair.
        Assert.Equal(80, row.InSample.Coverage.Observations);
        Assert.Equal(40, row.OutOfSample.Coverage.Observations);
        Assert.Equal(0, row.ObservationsWithoutForwardPrice);

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
            ComparisonFixtures.Options());

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
            ComparisonFixtures.Options());

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
            ComparisonFixtures.Options());

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
            ComparisonFixtures.Options());

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

        var leaderboard = Harness.Compare([withoutPrices], ComparisonFixtures.Options());

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

        var leaderboard = Harness.Compare([duplicatedAndPartlyPriceless], ComparisonFixtures.Options());

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

        var leaderboard = Harness.Compare([withAPricelessShadow], ComparisonFixtures.Options());

        var row = Assert.Single(leaderboard.Rows);
        Assert.Equal(0, row.ObservationsWithoutForwardPrice);

        // Every company-day is still scored exactly once — the shadow series adds no observation either.
        Assert.Equal(80, row.InSample.Coverage.Observations);
        Assert.Equal(4, row.InSample.Coverage.DistinctCompanies);
    }

    [Fact]
    public void Compare_WithNoHistoryProducesAnHonestEmptyLeaderboardRatherThanThrowing()
    {
        var leaderboard = Harness.Compare([], ComparisonFixtures.Options());

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
            ComparisonFixtures.Options());

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
            ComparisonFixtures.Options());

        var reversed = Harness.Compare(
            [
                ComparisonFixtures.Strategy("date-only", ComparisonFixtures.DateOnlyScore),
                ComparisonFixtures.Strategy("aligned", ComparisonFixtures.AlignedThroughout),
            ],
            ComparisonFixtures.Options());

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

        var a = Harness.Compare([byAsOf], ComparisonFixtures.Options());
        var b = Harness.Compare([shiftedRunDate], ComparisonFixtures.Options());

        Assert.Equal(a.Rows[0].InSample.Correlation.Rho, b.Rows[0].InSample.Correlation.Rho, 12);
        Assert.Equal(a.Windows, b.Windows);
    }
}
