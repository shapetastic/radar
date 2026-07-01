using Radar.Infrastructure.UsaSpending;

namespace Radar.Infrastructure.Tests.UsaSpending;

public sealed class UsaSpendingFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsExactPairPreservingSpaces()
    {
        var target = UsaSpendingFeedTarget.Parse(
            "recipientId=af09eaba-71de-97b6-660d-1adac9349c4d-C&recipientSearchText=Mercury Systems");

        Assert.NotNull(target);
        Assert.Equal("af09eaba-71de-97b6-660d-1adac9349c4d-C", target.RecipientId);
        // The literal space in the recipient name is preserved (the token is not URL-decoded).
        Assert.Equal("Mercury Systems", target.RecipientSearchText);
    }

    [Fact]
    public void Parse_SearchTextBeforeId_IsRobustToKeyOrdering()
    {
        var target = UsaSpendingFeedTarget.Parse(
            "recipientSearchText=Cryoport Systems&recipientId=c13d9361-755f-12da-2e17-8dda387a4a8f-C");

        Assert.NotNull(target);
        Assert.Equal("c13d9361-755f-12da-2e17-8dda387a4a8f-C", target.RecipientId);
        Assert.Equal("Cryoport Systems", target.RecipientSearchText);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = UsaSpendingFeedTarget.Parse(
            "  recipientId=abc-C&recipientSearchText=Agilysys  ");

        Assert.NotNull(target);
        Assert.Equal("abc-C", target.RecipientId);
        Assert.Equal("Agilysys", target.RecipientSearchText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/rss")]
    [InlineData("recipientId=abc-C")]
    [InlineData("recipientSearchText=Mercury Systems")]
    [InlineData("recipientId=&recipientSearchText=Mercury Systems")]
    [InlineData("recipientId=abc-C&recipientSearchText=")]
    public void Parse_MalformedOrMissingKey_ReturnsNull(string? token)
    {
        Assert.Null(UsaSpendingFeedTarget.Parse(token));
    }
}
