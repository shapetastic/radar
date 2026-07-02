using Radar.Infrastructure.Gdelt;

namespace Radar.Infrastructure.Tests.Gdelt;

public sealed class GdeltFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsPhrasePreservingSpacesAndTicker()
    {
        var target = GdeltFeedTarget.Parse("query=Mercury Systems&ticker=MRCY");

        Assert.NotNull(target);
        // The literal space in the phrase is preserved (the token is not URL-decoded).
        Assert.Equal("Mercury Systems", target.QueryPhrase);
        Assert.Equal("MRCY", target.Ticker);
    }

    [Fact]
    public void Parse_TickerBeforeQuery_IsRobustToKeyOrdering()
    {
        var target = GdeltFeedTarget.Parse("ticker=HLIO&query=Helios Technologies");

        Assert.NotNull(target);
        Assert.Equal("Helios Technologies", target.QueryPhrase);
        Assert.Equal("HLIO", target.Ticker);
    }

    [Fact]
    public void Parse_QueryOnly_ReturnsPhraseWithNullTicker()
    {
        var target = GdeltFeedTarget.Parse("query=Energy Recovery");

        Assert.NotNull(target);
        Assert.Equal("Energy Recovery", target.QueryPhrase);
        Assert.Null(target.Ticker);
    }

    [Fact]
    public void Parse_EmptyTickerValue_TreatedAsNoTicker()
    {
        var target = GdeltFeedTarget.Parse("query=Sapiens International&ticker=");

        Assert.NotNull(target);
        Assert.Equal("Sapiens International", target.QueryPhrase);
        Assert.Null(target.Ticker);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = GdeltFeedTarget.Parse("  query=Agilysys&ticker=AGYS  ");

        Assert.NotNull(target);
        Assert.Equal("Agilysys", target.QueryPhrase);
        Assert.Equal("AGYS", target.Ticker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/rss")]
    [InlineData("ticker=MRCY")]
    [InlineData("query=")]
    [InlineData("query=&ticker=MRCY")]
    public void Parse_MalformedOrMissingQuery_ReturnsNull(string? token)
    {
        Assert.Null(GdeltFeedTarget.Parse(token));
    }
}
