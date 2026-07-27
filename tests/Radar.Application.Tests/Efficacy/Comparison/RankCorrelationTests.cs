using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

/// <summary>
/// Spearman ρ and the closed-form Fisher-z interval, checked against HAND-COMPUTED values (not against a
/// second run of the same code) and with every degenerate case named rather than producing NaN.
/// </summary>
public sealed class RankCorrelationTests
{
    private const double Z95 = StrategyComparisonOptions.NormalQuantile95;

    [Fact]
    public void AverageRanks_AssignsTheMeanRankAcrossATiedRun()
    {
        // Sorted: 1 (idx 2) → rank 1; 5, 5 (idx 0, 1) → positions 2 and 3 ⇒ mean 2.5; 9 (idx 3) → rank 4.
        Assert.Equal([2.5, 2.5, 1.0, 4.0], RankCorrelation.AverageRanks([5.0, 5.0, 1.0, 9.0]));

        // All tied ⇒ every rank is the mean of 1..4.
        Assert.Equal([2.5, 2.5, 2.5, 2.5], RankCorrelation.AverageRanks([7.0, 7.0, 7.0, 7.0]));

        // Strictly increasing ⇒ 1..n, and order of appearance does not matter.
        Assert.Equal([4.0, 1.0, 3.0, 2.0], RankCorrelation.AverageRanks([40.0, 10.0, 30.0, 20.0]));
    }

    [Fact]
    public void Compute_MatchesTheHandComputedSpearmanRhoWithoutTies()
    {
        // x ranks 1,2,3,4,5; y ranks 1,2,3,5,4 ⇒ Σd² = 2 ⇒ ρ = 1 − 6·2/(5·(25−1)) = 1 − 12/120 = 0.9.
        var result = RankCorrelation.Compute(
            [1.0, 2.0, 3.0, 4.0, 5.0],
            [1.0, 2.0, 3.0, 5.0, 4.0],
            Z95);

        Assert.True(result.IsDefined);
        Assert.Equal(5, result.ObservationCount);
        Assert.Equal(0.9, result.Rho, 12);
    }

    [Fact]
    public void Compute_MatchesTheHandComputedSpearmanRhoWithTies()
    {
        // x = [1,1,2,3] ⇒ ranks [1.5,1.5,3,4]; y strictly increasing ⇒ ranks [1,2,3,4].
        // Deviations about the shared mean 2.5: dx = [−1,−1,0.5,1.5], dy = [−1.5,−0.5,0.5,1.5].
        // Sxy = 4.5, Sxx = 4.5, Syy = 5 ⇒ ρ = 4.5/√22.5 = 3/√10.
        var result = RankCorrelation.Compute(
            [1.0, 1.0, 2.0, 3.0],
            [10.0, 20.0, 30.0, 40.0],
            Z95);

        Assert.True(result.IsDefined);
        Assert.Equal(3.0 / Math.Sqrt(10.0), result.Rho, 12);
    }

    [Fact]
    public void Compute_IsInvariantToAMonotoneTransformOfEitherVector()
    {
        var raw = RankCorrelation.Compute(
            [1.0, 2.0, 3.0, 4.0, 5.0], [3.0, 1.0, 4.0, 1.5, 9.0], Z95);

        // exp() is strictly increasing, so every rank — and therefore ρ — is unchanged.
        var transformed = RankCorrelation.Compute(
            [1.0, 2.0, 3.0, 4.0, 5.0],
            [Math.Exp(3.0), Math.Exp(1.0), Math.Exp(4.0), Math.Exp(1.5), Math.Exp(9.0)],
            Z95);

        Assert.Equal(raw.Rho, transformed.Rho, 12);
    }

    [Fact]
    public void Compute_ProducesTheFisherZIntervalExactlyAsDefined()
    {
        var result = RankCorrelation.Compute(
            [1.0, 2.0, 3.0, 4.0, 5.0],
            [1.0, 2.0, 3.0, 5.0, 4.0],
            Z95);

        Assert.True(result.IsDefined);

        // The DEFINING property, asserted on the inverse transform rather than by re-running the formula:
        // atanh(bound) is exactly atanh(ρ) ± z/√(n−3).
        var expectedHalfWidth = Z95 / Math.Sqrt(5 - 3.0);
        Assert.Equal(Math.Atanh(result.Rho) - expectedHalfWidth, Math.Atanh(result.LowerBound), 10);
        Assert.Equal(Math.Atanh(result.Rho) + expectedHalfWidth, Math.Atanh(result.UpperBound), 10);

        // …and it is a genuine interval strictly inside (−1, 1).
        Assert.True(result.LowerBound < result.Rho);
        Assert.True(result.Rho < result.UpperBound);
        Assert.True(result.LowerBound > -1.0 && result.UpperBound < 1.0);
    }

    [Fact]
    public void Compute_IntervalNarrowsAsObservationsAccumulate()
    {
        // Same ρ (perfectly co-monotone up to one swap is not needed — repeat the pattern), more n.
        var small = RankCorrelation.Compute(
            [1.0, 2.0, 3.0, 4.0, 5.0], [1.0, 2.0, 3.0, 5.0, 4.0], Z95);

        var large = RankCorrelation.Compute(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10.0],
            [1, 2, 3, 5, 4, 6, 7, 8, 10, 9.0],
            Z95);

        Assert.True(small.IsDefined && large.IsDefined);
        Assert.True(
            large.UpperBound - large.LowerBound < small.UpperBound - small.LowerBound,
            "More observations must produce a narrower Fisher-z interval.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Compute_IsUndefinedBelowTheFisherZObservationFloor(int n)
    {
        var xs = Enumerable.Range(1, n).Select(i => (double)i).ToList();
        var ys = Enumerable.Range(1, n).Select(i => (double)(i * 3)).ToList();

        var result = RankCorrelation.Compute(xs, ys, Z95);

        Assert.False(result.IsDefined);
        Assert.Equal(RankCorrelationUndefinedReason.TooFewObservations, result.Reason);
        Assert.Equal(n, result.ObservationCount);
        Assert.Equal(0.0, result.Rho);
    }

    [Fact]
    public void Compute_NamesAConstantVectorRatherThanReturningNaN()
    {
        var constantScores = RankCorrelation.Compute(
            [5.0, 5.0, 5.0, 5.0, 5.0], [1.0, 2.0, 3.0, 4.0, 5.0], Z95);
        Assert.False(constantScores.IsDefined);
        Assert.Equal(RankCorrelationUndefinedReason.ConstantScores, constantScores.Reason);
        Assert.False(double.IsNaN(constantScores.Rho));

        var constantReturns = RankCorrelation.Compute(
            [1.0, 2.0, 3.0, 4.0, 5.0], [0.02, 0.02, 0.02, 0.02, 0.02], Z95);
        Assert.False(constantReturns.IsDefined);
        Assert.Equal(RankCorrelationUndefinedReason.ConstantReturns, constantReturns.Reason);
        Assert.False(double.IsNaN(constantReturns.Rho));
    }

    [Theory]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0 }, new[] { 10.0, 20.0, 30.0, 40.0 })]   // ρ = +1
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0 }, new[] { 40.0, 30.0, 20.0, 10.0 })]   // ρ = −1
    public void Compute_RefusesAZeroWidthIntervalAtPerfectCorrelation(double[] xs, double[] ys)
    {
        var result = RankCorrelation.Compute(xs, ys, Z95);

        // atanh(±1) is ±∞: the interval would collapse to a point and read as certainty over 4 observations.
        Assert.False(result.IsDefined);
        Assert.Equal(RankCorrelationUndefinedReason.PerfectCorrelation, result.Reason);
        Assert.Equal(4, result.ObservationCount);
        Assert.False(double.IsInfinity(result.LowerBound) || double.IsInfinity(result.UpperBound));
    }

    [Fact]
    public void Compute_RejectsMisalignedVectors()
    {
        Assert.Throws<ArgumentException>(() =>
            RankCorrelation.Compute([1.0, 2.0, 3.0, 4.0], [1.0, 2.0, 3.0], Z95));
    }
}
