using Radar.Application.Collectors;
using Radar.Domain.Companies;

namespace Radar.Application.Tests.Collectors;

public sealed class CollectionContextTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid CompanyA = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = new("00000000-0000-0000-0000-000000000002");

    private static CompanySourceFeed Feed(Guid id, Guid companyId, string feedType, string url) =>
        new(id, companyId, feedType, "Feed", url, FixedNow);

    private static CollectionContext Context(params CompanySourceFeed[] feeds) =>
        new([], feeds);

    [Fact]
    public void FeedsOfType_MixedTypes_ReturnsOnlyMatching()
    {
        var rss = Feed(new("00000000-0000-0000-0000-0000000000a1"), CompanyA, "rss", "https://a.test/rss");
        var sec = Feed(new("00000000-0000-0000-0000-0000000000a2"), CompanyA, "sec", "https://a.test/sec");
        var atom = Feed(new("00000000-0000-0000-0000-0000000000a3"), CompanyB, "atom", "https://b.test/atom");

        var result = Context(rss, sec, atom).FeedsOfType("rss");

        var feed = Assert.Single(result);
        Assert.Equal("https://a.test/rss", feed.Url);
    }

    [Fact]
    public void FeedsOfType_IsCaseInsensitive()
    {
        var rss = Feed(new("00000000-0000-0000-0000-0000000000b1"), CompanyA, "rss", "https://a.test/rss");

        var result = Context(rss).FeedsOfType("RSS");

        Assert.Single(result);
    }

    [Fact]
    public void FeedsOfType_OrdersByCompanyIdThenFeedId()
    {
        // Built deliberately out of order; expected order is (CompanyA, id 01), (CompanyA, id 02), (CompanyB, id 01).
        var companyBFirst = Feed(new("00000000-0000-0000-0000-000000000c01"), CompanyB, "rss", "b-1");
        var companyASecondId = Feed(new("00000000-0000-0000-0000-000000000c02"), CompanyA, "rss", "a-2");
        var companyAFirstId = Feed(new("00000000-0000-0000-0000-000000000c01"), CompanyA, "rss", "a-1");

        var result = Context(companyBFirst, companyASecondId, companyAFirstId).FeedsOfType("rss");

        Assert.Equal(["a-1", "a-2", "b-1"], result.Select(f => f.Url));
    }

    [Fact]
    public void FeedsOfType_NoMatch_ReturnsEmpty()
    {
        var rss = Feed(new("00000000-0000-0000-0000-0000000000d1"), CompanyA, "rss", "https://a.test/rss");

        var result = Context(rss).FeedsOfType("sec");

        Assert.Empty(result);
    }

    [Fact]
    public void FeedsOfType_NullArgument_Throws()
    {
        // Contract is "throws on null/whitespace"; assert any ArgumentException (the guard throws the
        // ArgumentNullException subtype for null) so the test isn't coupled to the exact subtype.
        Assert.ThrowsAny<ArgumentException>(() => Context().FeedsOfType(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FeedsOfType_BlankArgument_Throws(string feedType)
    {
        Assert.Throws<ArgumentException>(() => Context().FeedsOfType(feedType));
    }
}
