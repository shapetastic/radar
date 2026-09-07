using Radar.Application.Scoring;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 212 §1: <see cref="LabelThresholds"/> is the single owner of the pre-212 60 / 40 and enforces
/// <c>0 &lt; Watch &lt; Investigate ≤ 100</c> in its constructor, so no composition path — config, code or
/// test — can mint an unreachable or all-labelling pair.
/// </summary>
public sealed class LabelThresholdsTests
{
    [Fact]
    public void Default_IsSixtyForty_TheOnlyDefinitionOfThePre212Lines()
    {
        Assert.Equal(60, LabelThresholds.Default.Investigate);
        Assert.Equal(40, LabelThresholds.Default.Watch);
        Assert.Equal(new LabelThresholds(60, 40), LabelThresholds.Default);
    }

    [Theory]
    [InlineData(20, 15)]
    [InlineData(2, 1)]
    [InlineData(100, 99)]
    [InlineData(100, 1)]
    public void ValidPairs_Construct(int investigate, int watch)
    {
        var lines = new LabelThresholds(investigate, watch);
        Assert.Equal(investigate, lines.Investigate);
        Assert.Equal(watch, lines.Watch);
    }

    [Fact]
    public void Watch_AtOrAboveInvestigate_Throws()
    {
        var equal = Assert.Throws<ArgumentOutOfRangeException>(() => new LabelThresholds(40, 40));
        Assert.Contains("strictly below", equal.Message, StringComparison.Ordinal);
        Assert.Contains("unreachable", equal.Message, StringComparison.Ordinal);

        var above = Assert.Throws<ArgumentOutOfRangeException>(() => new LabelThresholds(15, 20));
        Assert.Contains("strictly below", above.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Watch_NotPositive_Throws(int watch)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new LabelThresholds(60, watch));
        Assert.Contains("greater than 0", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(1000)]
    public void Investigate_AboveOneHundred_Throws(int investigate)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new LabelThresholds(investigate, 40));
        Assert.Contains("at most 100", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDefaultPair_IsDistinguishableFromNull_ByType_AndEqualToDefaultByValue()
    {
        // Spec 212 §1: on a strategy definition, null means "omitted" and an explicit (60, 40) means "chose
        // the defaults" — only the second satisfies the Lead requirement. Value equality still holds.
        LabelThresholds? omitted = null;
        LabelThresholds? chosen = new(60, 40);

        Assert.Null(omitted);
        Assert.NotNull(chosen);
        Assert.Equal(LabelThresholds.Default, chosen);
    }
}
