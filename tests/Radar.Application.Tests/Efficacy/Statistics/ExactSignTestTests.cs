using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Tests.Efficacy.Statistics;

public sealed class ExactSignTestTests
{
    [Fact]
    public void Compute_MatchesTheHandComputedDoubledSmallerTail()
    {
        // 3 positive, 1 negative: smaller tail = BinomCdf(1; 4, .5) = 5/16 ⇒ p = 10/16 = 0.625.
        var result = ExactSignTest.Compute([1.0, 0.5, 0.2, -0.3]);

        Assert.True(result.IsDefined);
        Assert.Equal(0.625, result.PValue, 15);
        Assert.Equal(4, result.EffectiveN);
        Assert.Equal(3, result.PositiveDeltas);
        Assert.Equal(1, result.NegativeDeltas);
        Assert.Equal(0, result.ZeroDeltasDropped);
    }

    [Fact]
    public void Compute_DropsExactZerosFromItsEffectiveNOnlyAndReportsThem()
    {
        // One exact zero: effective N = 2 (not 3); smaller tail = BinomCdf(0; 2, .5) = 1/4 ⇒ p = 0.5.
        var result = ExactSignTest.Compute([0.0, 1.0, 2.0]);

        Assert.True(result.IsDefined);
        Assert.Equal(2, result.EffectiveN);
        Assert.Equal(1, result.ZeroDeltasDropped);
        Assert.Equal(0.5, result.PValue, 15);
    }

    [Fact]
    public void Compute_BalancedSplit_ReportsExactlyOneNeverAbove()
    {
        // 2 vs 2: smaller tail = BinomCdf(2; 4, .5) = 11/16 ⇒ doubled = 1.375 ⇒ capped at exactly 1.
        var result = ExactSignTest.Compute([1.0, 2.0, -1.0, -2.0]);

        Assert.True(result.IsDefined);
        Assert.Equal(1.0, result.PValue);
    }

    [Fact]
    public void Compute_AllZeros_IsNamedUndefinedNotNaN()
    {
        var result = ExactSignTest.Compute([0.0, 0.0]);

        Assert.False(result.IsDefined);
        Assert.Equal(SignTestUndefinedReason.NoNonZeroDeltas, result.Reason);
        Assert.Equal(2, result.ZeroDeltasDropped);
        Assert.False(double.IsNaN(result.PValue));
    }
}
