using Radar.Application.Replay;

namespace Radar.Application.Tests.Replay;

/// <summary>
/// Spec 139 — the as-of series: deterministic shape, no fabricated trailing point, no silent cap, and
/// fail-fast on a range that describes nothing.
/// </summary>
public sealed class ReplaySeriesTests
{
    private static readonly DateTimeOffset From = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThreePointRange_EnumeratesFromThroughTo_Ascending()
    {
        var series = ReplaySeries.Create(From, From.AddDays(2), TimeSpan.FromDays(1));

        Assert.Equal(3, series.Count);
        Assert.Equal(
            [From, From.AddDays(1), From.AddDays(2)],
            series.Points);
    }

    [Fact]
    public void SinglePointRange_WhenFromEqualsTo()
    {
        var series = ReplaySeries.Create(From, From, TimeSpan.FromDays(1));

        Assert.Equal(From, Assert.Single(series.Points));
    }

    [Fact]
    public void PartialTrailingStep_IsNotRoundedUpIntoAnExtraAsOfPoint()
    {
        // 'to' lands 12h past the last whole-day boundary. Emitting a 3rd point at 'to' would invent a
        // scoring instant nobody asked for; the honest answer is the two boundaries that fit.
        var series = ReplaySeries.Create(From, From.AddDays(1).AddHours(12), TimeSpan.FromDays(1));

        Assert.Equal([From, From.AddDays(1)], series.Points);
        // The requested bound is still recorded verbatim, so the runner can log what was asked for.
        Assert.Equal(From.AddDays(1).AddHours(12), series.ToUtc);
    }

    [Fact]
    public void Bounds_AreNormalisedToUtc_PreservingTheInstant()
    {
        var offsetFrom = new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var series = ReplaySeries.Create(offsetFrom, offsetFrom.AddDays(1), TimeSpan.FromDays(1));

        Assert.Equal(TimeSpan.Zero, series.Points[0].Offset);
        Assert.Equal(From, series.Points[0]);
    }

    [Fact]
    public void Create_IsDeterministic_ForTheSameArguments()
    {
        var a = ReplaySeries.Create(From, From.AddDays(5), TimeSpan.FromHours(12));
        var b = ReplaySeries.Create(From, From.AddDays(5), TimeSpan.FromHours(12));

        Assert.Equal(a.Points, b.Points);
    }

    [Fact]
    public void LargeRange_IsNotSilentlyCapped()
    {
        // The spec forbids truncating without saying so, so a wide range yields every point and exposes the
        // count for the runner to log rather than clamping it.
        var series = ReplaySeries.Create(From, From.AddDays(365), TimeSpan.FromDays(1));

        Assert.Equal(366, series.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveStep_Throws(int stepDays)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => ReplaySeries.Create(From, From.AddDays(2), TimeSpan.FromDays(stepDays)));

        Assert.Contains("strictly positive", ex.Message);
    }

    [Fact]
    public void InvertedRange_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ReplaySeries.Create(From, From.AddDays(-1), TimeSpan.FromDays(1)));

        Assert.Contains("before", ex.Message);
    }
}
