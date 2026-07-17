using Microsoft.Extensions.Time.Testing;

using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

/// <summary>
/// Covers the shared global SEC pacer (<see cref="SecRequestPacer"/>): it spaces successive
/// <see cref="SecRequestPacer.WaitTurnAsync"/> calls at least <see cref="SecRateLimitOptions.MinInterval"/>
/// apart, driven by the injected <see cref="TimeProvider"/> so pacing is deterministic and offline. Because a
/// single pacer instance is shared by every SEC client's handler, spacing successive calls IS spacing the
/// aggregate *.sec.gov request rate of a whole run. A <see cref="TimeSpan.Zero"/> interval serializes but adds
/// no delay; a negative interval is rejected at construction.
/// </summary>
public sealed class SecRequestPacerTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    private static SecRequestPacer CreatePacer(TimeSpan minInterval, TimeProvider timeProvider) =>
        new(new SecRateLimitOptions { MinInterval = minInterval }, timeProvider);

    [Fact]
    public async Task WaitTurnAsync_FirstCall_ProceedsImmediately()
    {
        var fake = new FakeTimeProvider(Start);
        var pacer = CreatePacer(TimeSpan.FromMilliseconds(150), fake);

        // No prior request ⇒ the very first turn is granted without any pacing delay (the clock never advances).
        var task = pacer.WaitTurnAsync(CancellationToken.None);

        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task WaitTurnAsync_SecondCall_WaitsMinIntervalAfterFirst()
    {
        var fake = new FakeTimeProvider(Start);
        var pacer = CreatePacer(TimeSpan.FromMilliseconds(150), fake);

        await pacer.WaitTurnAsync(CancellationToken.None);

        // The second turn must be at least MinInterval after the first: the task does NOT complete until the
        // fake clock advances by the interval.
        var second = pacer.WaitTurnAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        fake.Advance(TimeSpan.FromMilliseconds(149));
        Assert.False(second.IsCompleted);

        fake.Advance(TimeSpan.FromMilliseconds(1));
        await second;
    }

    [Fact]
    public async Task WaitTurnAsync_SpacesSuccessiveCallsCumulatively()
    {
        var fake = new FakeTimeProvider(Start);
        var pacer = CreatePacer(TimeSpan.FromMilliseconds(150), fake);

        // First is immediate; each subsequent turn is granted only after another full interval elapses — this is
        // what bounds the AGGREGATE rate across every SEC client sharing this one pacer.
        await pacer.WaitTurnAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            var next = pacer.WaitTurnAsync(CancellationToken.None);
            Assert.False(next.IsCompleted);
            fake.Advance(TimeSpan.FromMilliseconds(150));
            await next;
        }
    }

    [Fact]
    public async Task WaitTurnAsync_ZeroInterval_SerializesWithoutDelay()
    {
        var fake = new FakeTimeProvider(Start);
        var pacer = CreatePacer(TimeSpan.Zero, fake);

        // A zero interval still serializes callers (cheap) but adds no delay: every turn completes without ever
        // advancing the clock (reproduces un-paced throughput, e.g. for offline tests).
        for (var i = 0; i < 5; i++)
        {
            var task = pacer.WaitTurnAsync(CancellationToken.None);
            Assert.True(task.IsCompleted);
            await task;
        }
    }

    [Fact]
    public void Constructor_NegativeInterval_Throws()
    {
        var fake = new FakeTimeProvider(Start);

        var ex = Assert.Throws<InvalidOperationException>(
            () => CreatePacer(TimeSpan.FromMilliseconds(-1), fake));
        Assert.Contains("must not be negative", ex.Message, StringComparison.Ordinal);
    }
}
