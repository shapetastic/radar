using Radar.Application.Efficacy.Comparison;

namespace Radar.Application.Tests.Efficacy.Comparison;

public sealed class StrategyComparisonOptionsTests
{
    [Fact]
    public void Default_IsA21DayHorizonWithA30PercentHoldOutAndAFourDayExitTolerance()
    {
        Assert.Equal(21, StrategyComparisonOptions.Default.ForwardHorizonDays);
        Assert.Equal(0.30, StrategyComparisonOptions.Default.HoldOutFraction);
        Assert.Equal(20, StrategyComparisonOptions.Default.MinimumObservations);

        // 4 = the maximum shortfall measured over data/prices/ (3 days over 15,334 genuinely-complete 21-day
        // windows) plus one day of headroom for an unscheduled closure. It discards 0% of those windows.
        Assert.Equal(4, StrategyComparisonOptions.Default.ExitToleranceDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_RejectsANonPositiveHorizon(int horizon)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyComparisonOptions(horizon, 0.3, 20, 0));
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
            new StrategyComparisonOptions(21, fraction, 20, 4));
        Assert.Contains("HoldOutFraction", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Ctor_RejectsAMinimumBelowTheFisherZFloor(int minimum)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyComparisonOptions(21, 0.3, minimum, 4));
        Assert.Contains("MinimumObservations", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1/sqrt(n-3)", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]    // == the horizon: the coverage check would admit any bar after D
    [InlineData(22)]    // > the horizon: the minimum exit date would fall at or before D itself
    public void Ctor_RejectsAnExitToleranceOutsideZeroToBelowTheHorizon(int tolerance)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyComparisonOptions(21, 0.3, 20, tolerance));

        // The message must name the key an operator would have to edit.
        Assert.Contains(
            "Radar:Efficacy:Comparison:ExitToleranceDays", ex.Message, StringComparison.Ordinal);
        Assert.Contains("vacuous", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]    // horizon − 1: the loosest tolerance that still excludes something
    public void Ctor_AcceptsBothEndsOfTheValidExitToleranceRange(int tolerance)
    {
        var options = new StrategyComparisonOptions(21, 0.3, 20, tolerance);

        Assert.Equal(tolerance, options.ExitToleranceDays);
    }

    [Fact]
    public void Ctor_AcceptsTheFloorItself()
    {
        var options = new StrategyComparisonOptions(
            1, 0.5, StrategyComparisonOptions.MinimumObservationsFloor, 0);

        Assert.Equal(1, options.ForwardHorizonDays);
        Assert.Equal(0.5, options.HoldOutFraction);
        Assert.Equal(4, options.MinimumObservations);
        Assert.Equal(0, options.ExitToleranceDays);
    }
}
