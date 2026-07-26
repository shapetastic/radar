using Radar.Application.Replay;

namespace Radar.Application.Tests.Replay;

/// <summary>
/// Spec 139 — the replay label is a storage directory segment, so it is validated at construction against the
/// SAME shared rule the scoring-strategy names use. A label that could escape its root must be rejected
/// before any scoring happens, not discovered when a file lands somewhere unexpected.
/// </summary>
public sealed class ReplayPlanTests
{
    private static ReplaySeries Series() => ReplaySeries.Create(
        new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
        TimeSpan.FromDays(1));

    [Fact]
    public void UsableLabel_IsAccepted()
    {
        var plan = new ReplayPlan("20260501-20260503-1d", Series());

        Assert.Equal("20260501-20260503-1d", plan.Label);
        Assert.Equal(3, plan.Series.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLabel_Throws(string label)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ReplayPlan(label, Series()));

        Assert.Contains("non-blank Label", ex.Message);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData("a\0b")]
    public void UnusableLabel_Throws(string label)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ReplayPlan(label, Series()));

        Assert.Contains("storage directory segment", ex.Message);
    }

    [Fact]
    public void NullSeries_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayPlan("run", null!));
    }
}
