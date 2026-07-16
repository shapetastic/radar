using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

public sealed class ExponentialBackoffTests
{
    [Theory]
    [InlineData(0, 2)]      // attempt 0 -> base
    [InlineData(1, 4)]      // attempt 1 -> 2x base
    [InlineData(2, 8)]      // attempt 2 -> 4x base
    [InlineData(3, 16)]     // attempt 3 -> 8x base
    public void Compute_GrowsExponentially(int attempt, int expectedSeconds)
    {
        var backoff = ExponentialBackoff.Compute(TimeSpan.FromSeconds(2), attempt);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), backoff);
    }

    [Fact]
    public void Compute_ZeroBase_StaysZero()
    {
        Assert.Equal(TimeSpan.Zero, ExponentialBackoff.Compute(TimeSpan.Zero, 5));
    }

    [Fact]
    public void Compute_HugeAttempt_ClampsToMaxDelay_NeverOverflows()
    {
        // A large attempt count would overflow TimeSpan if computed naively; it must clamp to MaxDelay (10 min).
        var backoff = ExponentialBackoff.Compute(TimeSpan.FromSeconds(60), 40);

        Assert.Equal(ExponentialBackoff.MaxDelay, backoff);
        Assert.Equal(TimeSpan.FromMinutes(10), backoff);
    }
}
