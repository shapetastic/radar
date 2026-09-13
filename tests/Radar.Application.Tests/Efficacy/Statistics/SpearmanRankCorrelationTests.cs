using Radar.Application.Efficacy.Comparison;
using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Tests.Efficacy.Statistics;

/// <summary>
/// The Spearman core extracted by spec 225: average ranks on ties, ±1 on perfect monotone series, and a
/// NAMED degeneracy (never 0 or NaN) for constant or too-short input. The last test pins that the
/// price-facing <see cref="RankCorrelation"/> now computes THROUGH it — same bits, one code path.
/// </summary>
public sealed class SpearmanRankCorrelationTests
{
    [Fact]
    public void PerfectlyMonotoneIncreasing_IsPlusOne()
    {
        var r = SpearmanRankCorrelation.Compute([1.0, 2.0, 3.0, 4.0], [10.0, 20.0, 30.0, 40.0]);
        Assert.True(r.IsDefined);
        Assert.Equal(1.0, r.Rho!.Value, 12);
        Assert.Equal(4, r.ObservationCount);
        Assert.Equal(SpearmanDegeneracy.None, r.Degeneracy);
    }

    [Fact]
    public void PerfectlyReversed_IsMinusOne()
    {
        var r = SpearmanRankCorrelation.Compute([1.0, 2.0, 3.0, 4.0], [40.0, 30.0, 20.0, 10.0]);
        Assert.Equal(-1.0, r.Rho!.Value, 12);
    }

    [Fact]
    public void Ties_TakeAverageRanks_SoTheRankTotalIsInvariant()
    {
        // [5, 5, 7] → ranks 1.5, 1.5, 3 (sum 6 = 1+2+3).
        var ranks = SpearmanRankCorrelation.AverageRanks([5.0, 5.0, 7.0]);
        Assert.Equal([1.5, 1.5, 3.0], ranks);

        // The worked example the spec-225 counterfactual test relies on: EC ranks 1,4,3,2 vs
        // distinct-source-type ranks 1.5,4,1.5,3 → ρ = 3 / sqrt(5 · 4.5).
        var r = SpearmanRankCorrelation.Compute([40.0, 75.0, 60.0, 45.0], [1.0, 3.0, 1.0, 2.0]);
        Assert.Equal(3.0 / Math.Sqrt(5.0 * 4.5), r.Rho!.Value, 12);
    }

    [Fact]
    public void AConstantSeries_IsUndefined_AndNamesWhichSide()
    {
        var first = SpearmanRankCorrelation.Compute([2.0, 2.0, 2.0], [1.0, 2.0, 3.0]);
        Assert.False(first.IsDefined);
        Assert.Null(first.Rho);
        Assert.Equal(SpearmanDegeneracy.ConstantFirst, first.Degeneracy);

        var second = SpearmanRankCorrelation.Compute([1.0, 2.0, 3.0], [9.0, 9.0, 9.0]);
        Assert.Equal(SpearmanDegeneracy.ConstantSecond, second.Degeneracy);
    }

    [Fact]
    public void FewerThanTwoObservations_IsUndefined_NotZero()
    {
        var r = SpearmanRankCorrelation.Compute([1.0], [1.0]);
        Assert.Null(r.Rho);
        Assert.Equal(SpearmanDegeneracy.TooFewObservations, r.Degeneracy);
        Assert.Equal(1, r.ObservationCount);

        var empty = SpearmanRankCorrelation.Compute([], []);
        Assert.Equal(SpearmanDegeneracy.TooFewObservations, empty.Degeneracy);
    }

    [Fact]
    public void MisalignedVectors_Throw()
    {
        Assert.Throws<ArgumentException>(() => SpearmanRankCorrelation.Compute([1.0, 2.0], [1.0]));
    }

    [Fact]
    public void TheComparisonModule_ComputesThroughTheSameCore_BitForBit()
    {
        double[] x = [3.1, 1.7, 4.4, 1.7, 2.9, 8.0];
        double[] y = [0.2, -0.1, 0.5, 0.05, 0.2, 0.9];

        var core = SpearmanRankCorrelation.Compute(x, y);
        var viaComparison = RankCorrelation.ComputeRho(x, y);

        Assert.True(viaComparison.IsDefined);
        Assert.Equal(core.Rho!.Value, viaComparison.Rho);
        Assert.Equal(SpearmanRankCorrelation.AverageRanks(x), RankCorrelation.AverageRanks(x));

        // And the degeneracy names map one-to-one.
        Assert.Equal(
            RankCorrelationUndefinedReason.ConstantScores,
            RankCorrelation.ComputeRho([1.0, 1.0], [1.0, 2.0]).Reason);
        Assert.Equal(
            RankCorrelationUndefinedReason.ConstantReturns,
            RankCorrelation.ComputeRho([1.0, 2.0], [1.0, 1.0]).Reason);
        Assert.Equal(
            RankCorrelationUndefinedReason.TooFewObservations,
            RankCorrelation.ComputeRho([1.0], [1.0]).Reason);
    }
}
