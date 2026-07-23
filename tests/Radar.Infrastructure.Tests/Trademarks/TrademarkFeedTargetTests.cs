using Radar.Infrastructure.Trademarks;

namespace Radar.Infrastructure.Tests.Trademarks;

public sealed class TrademarkFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsOwnerPreservingSpacesAndHyphens()
    {
        var target = TrademarkFeedTarget.Parse("owner=WD-40 Company");

        Assert.NotNull(target);
        // The literal spaces and hyphen in the owner name are preserved (the token is not URL-decoded).
        Assert.Equal("WD-40 Company", target.OwnerName);
    }

    [Fact]
    public void Parse_ValueWithCommas_IsPreserved()
    {
        var target = TrademarkFeedTarget.Parse("owner=Steven Madden, Ltd.");

        Assert.NotNull(target);
        Assert.Equal("Steven Madden, Ltd.", target.OwnerName);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = TrademarkFeedTarget.Parse("  owner=Hormel Foods Corporation  ");

        Assert.NotNull(target);
        Assert.Equal("Hormel Foods Corporation", target.OwnerName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/rss")]
    [InlineData("assignee=Mercury Systems, Inc.")]
    [InlineData("owner=")]
    [InlineData("owner=   ")]
    public void Parse_MalformedOrMissingKey_ReturnsNull(string? token)
    {
        Assert.Null(TrademarkFeedTarget.Parse(token));
    }
}
