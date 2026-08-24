using Radar.Application.Ai;

namespace Radar.Application.Tests.Ai;

/// <summary>
/// Spec 187 §7's shared provider-latency primitive. The percentile DEFINITION is the point of these tests:
/// a percentile whose definition is not pinned is an unreproducible number, and two stages computing it
/// two ways would silently disagree about the same provider.
/// </summary>
public sealed class ProviderCallTimingsTests
{
    private static ProviderCallTimings WithMs(params double[] durationsMs)
    {
        var timings = new ProviderCallTimings();
        foreach (var ms in durationsMs)
        {
            timings.Record(TimeSpan.FromMilliseconds(ms));
        }

        return timings;
    }

    [Fact]
    public void NearestRank_IsCeilOfPercentileTimesCount_OneBased()
    {
        // The pinned vector, deliberately unsorted on input: 10 values, so rank(p50) = ceil(0.50 × 10) = 5
        // and rank(p95) = ceil(0.95 × 10) = 10. Sorted ascending that is
        // [10, 20, 30, 40, 50, 60, 70, 80, 90, 1000] ⇒ p50 = 50 (the 5th), p95 = 1000 (the 10th).
        var summary = WithMs(50, 20, 1000, 10, 90, 30, 80, 40, 70, 60).Summarize();

        Assert.Equal(10, summary.Calls);
        Assert.Equal(50d, summary.P50Ms);
        Assert.Equal(1000d, summary.P95Ms);
        Assert.Equal(1000d, summary.MaxMs);
        Assert.Equal(1450d, summary.TotalMs);
    }

    [Fact]
    public void NearestRank_OnASingleCall_ReportsThatCallForEveryPercentile()
    {
        var summary = WithMs(42).Summarize();

        Assert.Equal(1, summary.Calls);
        Assert.Equal(42d, summary.P50Ms);
        Assert.Equal(42d, summary.P95Ms);
        Assert.Equal(42d, summary.MaxMs);
        Assert.Equal(42d, summary.TotalMs);
    }

    [Theory]
    // n = 4: rank(p50) = ceil(2.0) = 2 ⇒ the 2nd smallest; rank(p95) = ceil(3.8) = 4 ⇒ the largest.
    [InlineData(50, 20d)]
    [InlineData(95, 40d)]
    // The boundaries are clamped into [1, n] rather than throwing or wrapping.
    [InlineData(0, 10d)]
    [InlineData(100, 40d)]
    public void NearestRank_ClampsTheRankIntoRange(int percentile, double expectedMs)
    {
        TimeSpan[] ascending =
        [
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40),
        ];

        Assert.Equal(expectedMs, ProviderCallTimings.NearestRankMs(ascending, percentile));
    }

    [Fact]
    public void ZeroCalls_RenderZeroCalls_NotInventedLatency()
    {
        var summary = new ProviderCallTimings().Summarize();

        Assert.Same(ProviderCallTimingSummary.NoCalls, summary);
        Assert.Equal(0, summary.Calls);

        // The DECISION (spec 187 §7): omit the percentiles entirely. A "p50 0.0 ms" would read as a
        // measured call that took no time, which is a different — and false — claim.
        var text = summary.Describe();
        Assert.Equal("0 provider call(s); no call latency measured this pass", text);
        Assert.DoesNotContain("p50", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p95", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_RendersCallsPercentilesMaxAndTotal_InInvariantCulture()
    {
        Assert.Equal(
            "3 provider call(s); p50 20.0 ms, p95 30.0 ms, max 30.0 ms, total 60.0 ms",
            WithMs(10, 20, 30).Summarize().Describe());
    }

    [Fact]
    public void RollingMeanAndMax_TrackEveryRecordedCall()
    {
        var timings = WithMs(10, 30);

        Assert.Equal(2, timings.Calls);
        Assert.Equal(20d, timings.MeanMs);
        Assert.Equal(TimeSpan.FromMilliseconds(30), timings.Max);
        Assert.Equal(TimeSpan.FromMilliseconds(40), timings.Total);
    }

    [Fact]
    public void RollingMeanAndMax_AreZeroBeforeAnyCall()
    {
        var timings = new ProviderCallTimings();

        Assert.Equal(0, timings.Calls);
        Assert.Equal(0d, timings.MeanMs);
        Assert.Equal(TimeSpan.Zero, timings.Max);
        Assert.Equal(TimeSpan.Zero, timings.Total);
    }

    [Fact]
    public void ANegativeDuration_IsRejected_RatherThanClamped()
    {
        // The monotonic APIs cannot produce one, so it can only mean the caller measured with the wrong
        // clock — and a summary that silently absorbed it would be a fiction.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProviderCallTimings().Record(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void TotalAccumulatesInTicks_SoRepeatedSmallCallsDoNotDrift()
    {
        var timings = new ProviderCallTimings();
        for (var i = 0; i < 1000; i++)
        {
            timings.Record(TimeSpan.FromTicks(7));
        }

        Assert.Equal(TimeSpan.FromTicks(7000), timings.Total);
    }
}
