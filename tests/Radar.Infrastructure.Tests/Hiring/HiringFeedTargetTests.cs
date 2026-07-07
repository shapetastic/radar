using Radar.Infrastructure.Hiring;

namespace Radar.Infrastructure.Tests.Hiring;

public sealed class HiringFeedTargetTests
{
    [Fact]
    public void Parse_CanonicalToken_YieldsPlatformAndBoard()
    {
        var target = HiringFeedTarget.Parse("platform=greenhouse&board=mercury");

        Assert.NotNull(target);
        Assert.Equal("greenhouse", target!.Platform);
        Assert.Equal("mercury", target.BoardToken);
    }

    [Fact]
    public void Parse_ReversedKeyOrder_YieldsSameTarget()
    {
        var target = HiringFeedTarget.Parse("board=mercury&platform=greenhouse");

        Assert.NotNull(target);
        Assert.Equal("greenhouse", target!.Platform);
        Assert.Equal("mercury", target.BoardToken);
    }

    [Fact]
    public void Parse_LeverToken_YieldsPlatformAndBoard()
    {
        var target = HiringFeedTarget.Parse("platform=lever&board=energyrecovery");

        Assert.NotNull(target);
        Assert.Equal("lever", target!.Platform);
        Assert.Equal("energyrecovery", target.BoardToken);
    }

    [Fact]
    public void Parse_SurroundingAndInnerWhitespace_IsTrimmed()
    {
        var target = HiringFeedTarget.Parse("  platform= greenhouse &board= commvault  ");

        Assert.NotNull(target);
        Assert.Equal("greenhouse", target!.Platform);
        Assert.Equal("commvault", target.BoardToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankToken_ReturnsNull(string? token)
    {
        Assert.Null(HiringFeedTarget.Parse(token));
    }

    [Theory]
    [InlineData("platform=greenhouse")]                  // board key missing
    [InlineData("board=mercury")]                        // platform key missing
    [InlineData("platform=greenhouse&board=")]           // blank board value
    [InlineData("platform=&board=mercury")]              // blank platform value
    [InlineData("platform=greenhouse board=mercury")]    // no '&' boundary between the keys
    [InlineData("https://example.com/careers")]          // not a token at all
    public void Parse_MalformedToken_ReturnsNull(string token)
    {
        Assert.Null(HiringFeedTarget.Parse(token));
    }
}
