using Radar.Infrastructure.Patents;

namespace Radar.Infrastructure.Tests.Patents;

public sealed class PatentFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsAssigneePreservingSpacesAndCommas()
    {
        var target = PatentFeedTarget.Parse("assignee=Mercury Systems, Inc.");

        Assert.NotNull(target);
        // The literal spaces and comma in the legal entity name are preserved (the token is not URL-decoded).
        Assert.Equal("Mercury Systems, Inc.", target.AssigneeName);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = PatentFeedTarget.Parse("  assignee=Energy Recovery, Inc.  ");

        Assert.NotNull(target);
        Assert.Equal("Energy Recovery, Inc.", target.AssigneeName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/rss")]
    [InlineData("platform=greenhouse&board=mercury")]
    [InlineData("assignee=")]
    [InlineData("assignee=   ")]
    public void Parse_MalformedOrMissingKey_ReturnsNull(string? token)
    {
        Assert.Null(PatentFeedTarget.Parse(token));
    }
}
