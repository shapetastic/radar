using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

public sealed class StrategyComparisonOptionsTests
{
    [Fact]
    public void Default_IsA21DayHorizonWithA30PercentHoldOut()
    {
        Assert.Equal(21, StrategyComparisonOptions.Default.ForwardHorizonDays);
        Assert.Equal(0.30, StrategyComparisonOptions.Default.HoldOutFraction);
        Assert.Equal(20, StrategyComparisonOptions.Default.MinimumObservations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_RejectsANonPositiveHorizon(int horizon)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyComparisonOptions(horizon, 0.3, 20));
        Assert.Contains("ForwardHorizonDays", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void Ctor_RejectsAHoldOutFractionThatWouldEmptyAWindowByDefinition(double fraction)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyComparisonOptions(21, fraction, 20));
        Assert.Contains("HoldOutFraction", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Ctor_RejectsAMinimumBelowTheFisherZFloor(int minimum)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyComparisonOptions(21, 0.3, minimum));
        Assert.Contains("MinimumObservations", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1/sqrt(n-3)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_AcceptsTheFloorItself()
    {
        var options = new StrategyComparisonOptions(
            1, 0.5, StrategyComparisonOptions.MinimumObservationsFloor);

        Assert.Equal(1, options.ForwardHorizonDays);
        Assert.Equal(0.5, options.HoldOutFraction);
        Assert.Equal(4, options.MinimumObservations);
    }
}
