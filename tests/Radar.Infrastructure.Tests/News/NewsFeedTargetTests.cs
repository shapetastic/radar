using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

public sealed class NewsFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsPhrasePreservingSpacesAndTicker()
    {
        var target = NewsFeedTarget.Parse("query=Rocket Lab&ticker=RKLB");

        Assert.NotNull(target);
        // The literal space in the phrase is preserved (the token is not URL-decoded).
        Assert.Equal("Rocket Lab", target.QueryPhrase);
        Assert.Equal("RKLB", target.Ticker);
    }

    [Fact]
    public void Parse_TickerBeforeQuery_IsRobustToKeyOrdering()
    {
        var target = NewsFeedTarget.Parse("ticker=HLIO&query=Helios Technologies");

        Assert.NotNull(target);
        Assert.Equal("Helios Technologies", target.QueryPhrase);
        Assert.Equal("HLIO", target.Ticker);
    }

    [Fact]
    public void Parse_QueryOnly_ReturnsPhraseWithNullTicker()
    {
        var target = NewsFeedTarget.Parse("query=Energy Recovery");

        Assert.NotNull(target);
        Assert.Equal("Energy Recovery", target.QueryPhrase);
        Assert.Null(target.Ticker);
    }

    [Fact]
    public void Parse_EmptyTickerValue_TreatedAsNoTicker()
    {
        var target = NewsFeedTarget.Parse("query=Sapiens International&ticker=");

        Assert.NotNull(target);
        Assert.Equal("Sapiens International", target.QueryPhrase);
        Assert.Null(target.Ticker);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = NewsFeedTarget.Parse("  query=Agilysys&ticker=AGYS  ");

        Assert.NotNull(target);
        Assert.Equal("Agilysys", target.QueryPhrase);
        Assert.Equal("AGYS", target.Ticker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/rss")]
    [InlineData("ticker=RKLB")]
    [InlineData("query=")]
    [InlineData("query=&ticker=RKLB")]
    public void Parse_MalformedOrMissingQuery_ReturnsNull(string? token)
    {
        Assert.Null(NewsFeedTarget.Parse(token));
    }
}
