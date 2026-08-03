using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Tests.Efficacy.Statistics;

/// <summary>
/// The exact order-statistic median interval, checked against HAND-COMPUTED binomial values — never against a
/// second run of the same code.
/// </summary>
public sealed class ExactMedianIntervalTests
{
    [Fact]
    public void CdfAtHalf_MatchesHandComputedValues()
    {
        // n=6: P(X<=0) = 1/64; P(X<=2) = (1+6+15)/64 = 22/64.
        Assert.Equal(1.0 / 64.0, ExactBinomial.CdfAtHalf(0, 6), 15);
        Assert.Equal(22.0 / 64.0, ExactBinomial.CdfAtHalf(2, 6), 15);

        // n=4: P(X<=1) = (1+4)/16.
        Assert.Equal(5.0 / 16.0, ExactBinomial.CdfAtHalf(1, 4), 15);

        // The whole distribution sums to exactly 1.
        Assert.Equal(1.0, ExactBinomial.CdfAtHalf(6, 6), 15);
    }

    [Fact]
    public void Compute_SixBlocks_UsesTheExtremesWithCoverage096875()
    {
        // Hand-computed: 1 − 2·BinomCdf(0; 6, 0.5) = 1 − 2/64 = 0.96875 ≥ 0.95, and k=2 gives
        // 1 − 2·(7/64) = 0.78125 < 0.95 — so k = 1 and the interval is [min, max].
        var result = ExactMedianInterval.Compute([0.5, -0.1, 0.3, 0.2, 0.9, 0.4]);

        Assert.True(result.IsDefined);
        Assert.Equal(1, result.LowerOrderStatistic);
        Assert.Equal(-0.1, result.Lower);
        Assert.Equal(0.9, result.Upper);
        Assert.Equal(0.96875, result.AchievedCoverage, 15);
        Assert.Equal(6, result.BlockCount);
    }

    [Fact]
    public void Compute_FiveBlocks_IsInsufficientAndConfidenceIsNotRelaxed()
    {
        // n=5, k=1: 1 − 2/32 = 0.9375 < 0.95 — no k works; the answer is a named degeneracy, never a
        // weaker level and never NaN.
        var result = ExactMedianInterval.Compute([0.1, 0.2, 0.3, 0.4, 0.5]);

        Assert.False(result.IsDefined);
        Assert.Equal(MedianIntervalUndefinedReason.InsufficientPurgedBlocks, result.Reason);
        Assert.Equal(5, result.BlockCount);
        Assert.False(double.IsNaN(result.Lower));
        Assert.False(double.IsNaN(result.Upper));
    }

    [Fact]
    public void Compute_TwentyBlocks_ChoosesKSix()
    {
        // Hand-computed for n=20: BinomCdf(5; 20, .5) = 21700/1048576 ≈ 0.02069 ⇒ coverage 0.95861 ≥ .95;
        // BinomCdf(6; 20, .5) = 60460/1048576 ≈ 0.05766 ⇒ coverage 0.88469 < .95. So k = 6 and the interval
        // is [x_(6), x_(15)].
        var values = Enumerable.Range(1, 20).Select(i => (double)i).ToList();

        var result = ExactMedianInterval.Compute(values);

        Assert.True(result.IsDefined);
        Assert.Equal(6, result.LowerOrderStatistic);
        Assert.Equal(6.0, result.Lower);
        Assert.Equal(15.0, result.Upper);
        Assert.Equal(1.0 - (2.0 * (21700.0 / 1048576.0)), result.AchievedCoverage, 15);
    }

    [Fact]
    public void Compute_TiesAreDataAndStillYieldOrderStatistics()
    {
        // All-equal deltas: the interval collapses onto the tied value — conservative, defined, no NaN.
        var result = ExactMedianInterval.Compute([2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0]);

        Assert.True(result.IsDefined);
        Assert.Equal(2.0, result.Lower);
        Assert.Equal(2.0, result.Upper);
    }

    [Fact]
    public void Compute_IsOrderInvariantAndDeterministic()
    {
        double[] a = [0.5, -0.1, 0.3, 0.2, 0.9, 0.4, 0.7];
        var shuffledDeterministically = a.Reverse().ToArray();

        var first = ExactMedianInterval.Compute(a);
        var second = ExactMedianInterval.Compute(shuffledDeterministically);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_EmptyInput_IsInsufficientNotAThrow()
    {
        var result = ExactMedianInterval.Compute([]);

        Assert.False(result.IsDefined);
        Assert.Equal(MedianIntervalUndefinedReason.InsufficientPurgedBlocks, result.Reason);
    }

    [Fact]
    public void MedianOf_UsesTheStandardEvenOddConventions()
    {
        Assert.Equal(3.0, ExactMedianInterval.MedianOf([5.0, 1.0, 3.0]));
        Assert.Equal(2.5, ExactMedianInterval.MedianOf([4.0, 1.0, 2.0, 3.0]));
    }
}
