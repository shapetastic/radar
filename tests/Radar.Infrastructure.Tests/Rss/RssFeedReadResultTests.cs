using Radar.Infrastructure.Rss;

namespace Radar.Infrastructure.Tests.Rss;

public sealed class RssFeedReadResultTests
{
    [Fact]
    public void Failure_rejects_the_Success_outcome()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => RssFeedReadResult.Failure(RssFeedReadOutcome.Success, "should not be allowed"));

        Assert.Equal("outcome", ex.ParamName);
    }

    [Fact]
    public void Failure_carries_the_failure_outcome_and_detail()
    {
        var failureOutcomes = new[]
        {
            RssFeedReadOutcome.Unreachable,
            RssFeedReadOutcome.HttpError,
            RssFeedReadOutcome.Timeout,
            RssFeedReadOutcome.Malformed,
        };

        foreach (var outcome in failureOutcomes)
        {
            var result = RssFeedReadResult.Failure(outcome, "boom");

            Assert.False(result.IsSuccess);
            Assert.Equal(outcome, result.Outcome);
            Assert.Equal("boom", result.Detail);
            Assert.Empty(result.Items);
        }
    }
}
