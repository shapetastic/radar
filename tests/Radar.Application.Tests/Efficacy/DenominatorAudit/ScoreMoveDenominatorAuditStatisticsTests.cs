using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.DenominatorAudit;

namespace Radar.Application.Tests.Efficacy.DenominatorAudit;

/// <summary>
/// Pins the spec-172 statistics: the coefficients come from the SHARED <see cref="RankCorrelation.ComputeRho"/>
/// (whose ACTUAL behaviour is pinned here — floor of 2, named constant-vector degeneracies, and a DEFINED
/// perfect ±1, since no interval exists to collapse), plus the deterministic closed-form median / p90
/// conventions and the fixed ordered bins.
/// </summary>
public sealed class ScoreMoveDenominatorAuditStatisticsTests
{
    private static DenominatorObservation Observation(
        int deltaOpportunity, int directionalCount, int linkCount = 5) =>
        new(
            StrategyName: "default",
            CompanyId: Guid.NewGuid(),
            AsOfDate: new DateOnly(2026, 7, 1),
            DeltaOpportunity: deltaOpportunity,
            DeltaTrajectory: 0,
            LinkCount: linkCount,
            DirectionalCount: directionalCount);

    private static DenominatorAuditStrategyResult Compute(params DenominatorObservation[] observations) =>
        ScoreMoveDenominatorAudit.Compute("default", companiesWalked: 5, companiesWithPairs: 3, observations);

    // ------------------------------------------------------------------------------------------------
    // Degeneracy passthrough — named reasons, never NaN. ComputeRho's floor is 2 (the coefficient-only
    // floor; the "fewer than 4" floor belongs to interval-bearing correlations, which the audit never
    // computes). Do-not-modify-RankCorrelation is honoured by pinning its actual behaviour.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void FewerThanTwoObservations_IsNamedTooFewObservations_NeverNaN()
    {
        var result = Compute(Observation(10, 1));

        Assert.False(result.RhoAbsDeltaVsDirectionalCount.IsDefined);
        Assert.Equal(
            RankCorrelationUndefinedReason.TooFewObservations,
            result.RhoAbsDeltaVsDirectionalCount.Reason);
        Assert.Equal(1, result.RhoAbsDeltaVsDirectionalCount.ObservationCount);
        Assert.False(double.IsNaN(result.RhoAbsDeltaVsDirectionalCount.Rho));
        Assert.False(result.RhoAbsDeltaVsLinkCount.IsDefined);
    }

    [Fact]
    public void ConstantDirectionalCountVector_IsNamedConstantReturns_TheSecondVector()
    {
        // ComputeRho is called with first = |delta|, second = the count vector, so a constant
        // DirectionalCount surfaces as ConstantReturns in the shared vocabulary. Pinned deliberately.
        var result = Compute(Observation(3, 2), Observation(9, 2), Observation(21, 2));

        Assert.False(result.RhoAbsDeltaVsDirectionalCount.IsDefined);
        Assert.Equal(
            RankCorrelationUndefinedReason.ConstantReturns,
            result.RhoAbsDeltaVsDirectionalCount.Reason);
        Assert.False(double.IsNaN(result.RhoAbsDeltaVsDirectionalCount.Rho));
    }

    [Fact]
    public void ConstantAbsDeltaVector_IsNamedConstantScores_TheFirstVector()
    {
        // |−7| == |7|: the abs-delta vector is constant even though the signed deltas differ.
        var result = Compute(Observation(7, 1), Observation(-7, 2), Observation(7, 3));

        Assert.False(result.RhoAbsDeltaVsDirectionalCount.IsDefined);
        Assert.Equal(
            RankCorrelationUndefinedReason.ConstantScores,
            result.RhoAbsDeltaVsDirectionalCount.Reason);
    }

    [Fact]
    public void PerfectCorrelation_IsDefinedAtPlusMinusOne_BecauseNoIntervalIsComputed()
    {
        // ComputeRho's actual behaviour, pinned: a genuine |rho| = 1 is a usable coefficient here — the
        // PerfectCorrelation degeneracy exists only for interval-bearing computations.
        var negative = Compute(Observation(30, 0), Observation(20, 1), Observation(10, 2));
        Assert.True(negative.RhoAbsDeltaVsDirectionalCount.IsDefined);
        Assert.Equal(-1.0, negative.RhoAbsDeltaVsDirectionalCount.Rho);
        Assert.Equal(
            RankCorrelationUndefinedReason.None, negative.RhoAbsDeltaVsDirectionalCount.Reason);

        var positive = Compute(Observation(10, 0), Observation(20, 1), Observation(30, 2));
        Assert.True(positive.RhoAbsDeltaVsDirectionalCount.IsDefined);
        Assert.Equal(1.0, positive.RhoAbsDeltaVsDirectionalCount.Rho);
    }

    [Fact]
    public void BothCoefficientsAreComputed_DirectionalAndLinkCount_OverTheSameAbsDeltaVector()
    {
        // Directional falls as |delta| rises (the hypothesis, rho -1) while LinkCount rises with it
        // (rho +1): the two denominators genuinely answer different questions and must not be conflated.
        var result = Compute(
            Observation(10, 4, linkCount: 5),
            Observation(20, 2, linkCount: 9),
            Observation(30, 1, linkCount: 28));

        Assert.Equal(-1.0, result.RhoAbsDeltaVsDirectionalCount.Rho);
        Assert.Equal(1.0, result.RhoAbsDeltaVsLinkCount.Rho);
    }

    // ------------------------------------------------------------------------------------------------
    // Median / p90 conventions, pinned: sorted order statistics, closed-form, no interpolation.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Median_OddCount_IsTheMiddleOrderStatistic()
    {
        Assert.Equal(2.0, ScoreMoveDenominatorAudit.MedianOfSorted([1.0, 2.0, 9.0]));
    }

    [Fact]
    public void Median_EvenCount_IsTheMeanOfTheTwoMiddleOrderStatistics()
    {
        Assert.Equal(2.5, ScoreMoveDenominatorAudit.MedianOfSorted([1.0, 2.0, 3.0, 10.0]));
    }

    [Fact]
    public void Percentile90_IsNearestRank_CeilOfNineTenthsN()
    {
        // n = 10: ceil(9.0) = 9 → the 9th order statistic (1-based).
        Assert.Equal(
            9.0,
            ScoreMoveDenominatorAudit.Percentile90OfSorted(
                [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0]));

        // n = 5: ceil(4.5) = 5 → the maximum.
        Assert.Equal(50.0, ScoreMoveDenominatorAudit.Percentile90OfSorted([10.0, 20.0, 30.0, 40.0, 50.0]));

        // n = 1: the single value.
        Assert.Equal(7.0, ScoreMoveDenominatorAudit.Percentile90OfSorted([7.0]));
    }

    // ------------------------------------------------------------------------------------------------
    // Bins: fixed, ordered, and an empty bin survives with null statistics rather than being dropped.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Bins_AreStableAndOrdered_AndAnEmptyBinIsPresentWithNullStatistics()
    {
        var result = Compute(
            Observation(4, 0),
            Observation(8, 0),
            Observation(2, 1),
            Observation(6, 4),
            Observation(10, 7)); // 7 lands in the open-ended 4+ bin

        Assert.Equal(["0", "1", "2", "3", "4+"], result.Bins.Select(b => b.Label));

        var bin0 = result.Bins[0];
        Assert.Equal(2, bin0.Count);
        Assert.Equal(6.0, bin0.MedianAbsDeltaOpportunity); // mean of 4 and 8
        Assert.Equal(8.0, bin0.P90AbsDeltaOpportunity);    // nearest-rank over [4, 8]

        Assert.Equal(1, result.Bins[1].Count);
        Assert.Equal(2.0, result.Bins[1].MedianAbsDeltaOpportunity);

        // Bins 2 and 3 are EMPTY: present, zero count, null statistics — never dropped.
        Assert.Equal(0, result.Bins[2].Count);
        Assert.Null(result.Bins[2].MedianAbsDeltaOpportunity);
        Assert.Null(result.Bins[2].P90AbsDeltaOpportunity);
        Assert.Equal(0, result.Bins[3].Count);

        var bin4Plus = result.Bins[4];
        Assert.Equal(2, bin4Plus.Count); // DirectionalCount 4 and 7 both land here
        Assert.Equal(8.0, bin4Plus.MedianAbsDeltaOpportunity); // mean of 6 and 10
    }

    [Fact]
    public void NoObservations_ProducesAllEmptyBins_AndNamedDegenerateCoefficients()
    {
        var result = Compute();

        Assert.All(result.Bins, b =>
        {
            Assert.Equal(0, b.Count);
            Assert.Null(b.MedianAbsDeltaOpportunity);
            Assert.Null(b.P90AbsDeltaOpportunity);
        });
        Assert.Equal(
            RankCorrelationUndefinedReason.TooFewObservations,
            result.RhoAbsDeltaVsDirectionalCount.Reason);
    }
}
