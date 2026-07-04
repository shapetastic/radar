using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

public sealed class QueryFeedTargetTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsPhrasePreservingSpacesAndTicker()
    {
        // Retains a literal from the former GDELT world (Mercury Systems / MRCY).
        var target = QueryFeedTarget.Parse("query=Mercury Systems&ticker=MRCY");

        Assert.NotNull(target);
        // The literal space in the phrase is preserved (the token is not URL-decoded).
        Assert.Equal("Mercury Systems", target.QueryPhrase);
        Assert.Equal("MRCY", target.Ticker);
    }

    [Fact]
    public void Parse_ValidToken_FromNewsWorld_ReturnsPhrasePreservingSpacesAndTicker()
    {
        // Retains a literal from the former News world (Rocket Lab / RKLB) so neither collector's
        // coverage is silently dropped; the parser is source-agnostic.
        var target = QueryFeedTarget.Parse("query=Rocket Lab&ticker=RKLB");

        Assert.NotNull(target);
        Assert.Equal("Rocket Lab", target.QueryPhrase);
        Assert.Equal("RKLB", target.Ticker);
    }

    [Fact]
    public void Parse_TickerBeforeQuery_IsRobustToKeyOrdering()
    {
        var target = QueryFeedTarget.Parse("ticker=HLIO&query=Helios Technologies");

        Assert.NotNull(target);
        Assert.Equal("Helios Technologies", target.QueryPhrase);
        Assert.Equal("HLIO", target.Ticker);
    }

    [Fact]
    public void Parse_QueryOnly_ReturnsPhraseWithNullTicker()
    {
        var target = QueryFeedTarget.Parse("query=Energy Recovery");

        Assert.NotNull(target);
        Assert.Equal("Energy Recovery", target.QueryPhrase);
        Assert.Null(target.Ticker);
    }

    [Fact]
    public void Parse_EmptyTickerValue_TreatedAsNoTicker()
    {
        var target = QueryFeedTarget.Parse("query=Sapiens International&ticker=");

        Assert.NotNull(target);
        Assert.Equal("Sapiens International", target.QueryPhrase);
        Assert.Null(target.Ticker);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsTrimmed()
    {
        var target = QueryFeedTarget.Parse("  query=Agilysys&ticker=AGYS  ");

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
        Assert.Null(QueryFeedTarget.Parse(token));
    }
}
