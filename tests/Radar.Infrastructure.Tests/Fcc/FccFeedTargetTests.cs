using Radar.Infrastructure.Fcc;

namespace Radar.Infrastructure.Tests.Fcc;

public sealed class FccFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsGranteePreservingSpacesAndCommas()
    {
        var target = FccFeedTarget.Parse("grantee=Rocket Lab USA, Inc.");

        Assert.NotNull(target);
        // The literal spaces and comma in the grantee name are preserved (the token is not URL-decoded).
        Assert.Equal("Rocket Lab USA, Inc.", target.GranteeName);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = FccFeedTarget.Parse("  grantee=Mercury Systems, Inc.  ");

        Assert.NotNull(target);
        Assert.Equal("Mercury Systems, Inc.", target.GranteeName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/rss")]
    [InlineData("assignee=Mercury Systems, Inc.")]
    [InlineData("grantee=")]
    [InlineData("grantee=   ")]
    public void Parse_MalformedOrMissingKey_ReturnsNull(string? token)
    {
        Assert.Null(FccFeedTarget.Parse(token));
    }
}
