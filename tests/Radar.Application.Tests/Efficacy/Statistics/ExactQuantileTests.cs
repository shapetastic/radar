using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Tests.Efficacy.Statistics;

/// <summary>
/// The shared nearest-rank quantile: exact, deterministic, and deliberately NOT the median definition (which
/// stays <see cref="ExactMedianInterval.MedianOf"/>, the repo's one median).
/// </summary>
public sealed class ExactQuantileTests
{
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.25, 1.0)]
    [InlineData(0.5, 2.0)]
    [InlineData(0.75, 3.0)]
    [InlineData(1.0, 4.0)]
    public void Of_ReturnsTheNearestRankOrderStatistic(double p, double expected) =>
        Assert.Equal(expected, ExactQuantile.Of([3.0, 1.0, 4.0, 2.0], p));

    [Fact]
    public void Of_ReturnsAValueThatIsInTheSample_NeverAnInterpolatedInvention()
    {
        double[] values = [10.0, 20.0];
        Assert.Contains(ExactQuantile.Of(values, 0.5), values);
        Assert.Contains(ExactQuantile.Of(values, 0.25), values);
    }

    [Fact]
    public void Of_IsDeterministic_AndDoesNotDependOnInputOrder() =>
        Assert.Equal(
            ExactQuantile.Of([5.0, 1.0, 3.0, 2.0, 4.0], 0.75),
            ExactQuantile.Of([1.0, 2.0, 3.0, 4.0, 5.0], 0.75));

    [Fact]
    public void Of_RefusesAnEmptySample_RatherThanReturningAZeroThatWouldReadAsMeasured() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ExactQuantile.Of([], 0.5));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Of_RefusesAProbabilityOutsideTheUnitInterval(double p) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ExactQuantile.Of([1.0, 2.0], p));

    [Fact]
    public void TheMedianConventionsDiffer_WhichIsWhyBothAreStatedBesideTheNumbers()
    {
        // Nearest-rank at p = 0.5 picks an order statistic; the repo's median averages the two middle ones.
        Assert.Equal(10.0, ExactQuantile.Of([10.0, 20.0], 0.5));
        Assert.Equal(15.0, ExactMedianInterval.MedianOf([10.0, 20.0]));
    }
}
