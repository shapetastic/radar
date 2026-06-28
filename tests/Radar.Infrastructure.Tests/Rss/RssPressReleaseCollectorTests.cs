using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Infrastructure.Rss;

namespace Radar.Infrastructure.Tests.Rss;

public sealed class RssPressReleaseCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Company Company(Guid id, string name, string? ticker) =>
        new(
            Id: id,
            Name: name,
            LegalName: null,
            Ticker: ticker,
            Exchange: null,
            CountryCode: null,
            Sector: null,
            Industry: null,
            Status: CompanyStatus.Active,
            CreatedAtUtc: FixedNow,
            UpdatedAtUtc: FixedNow,
            Themes: []);

    private static CompanySourceFeed Feed(Guid id, Guid companyId, string name, string url, string feedType = "rss") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static RssFeedItem Item(string title, string? link, string? summary = "summary", string? id = "id") =>
        new(id, title, summary, link, FixedNow);

    private static RssPressReleaseCollector CreateCollector(FakeRssFeedReader reader) =>
        new(reader, NullLogger<RssPressReleaseCollector>.Instance, new FixedTimeProvider(FixedNow));

    [Fact]
    public async Task CollectAsync_TwoFeeds_ProducesEvidencePerItemWithProvenanceAndHints()
    {
        var feedA = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), AcmeId, "Acme IR", "https://acme.test/rss");
        var feedB = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), GlobexId, "Globex IR", "https://globex.test/rss");

        var reader = new FakeRssFeedReader
        {
            ["https://acme.test/rss"] = [Item("Acme launches widget", "https://acme.test/n1")],
            ["https://globex.test/rss"] = [Item("Globex opens plant", "https://globex.test/n1")],
        };

        var context = new CollectionContext(
            [Company(AcmeId, "Acme Corp", "ACME"), Company(GlobexId, "Globex Inc", "GLBX")],
            [feedA, feedB]);

        var items = (await CreateCollector(reader).CollectAsync(context, CancellationToken.None)).ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("press_release", i.SourceType));

        var acme = items.Single(i => i.SourceName == "Acme IR");
        Assert.Equal("https://acme.test/n1", acme.SourceUrl);
        Assert.Equal("Acme launches widget", acme.Title);
        Assert.Equal("https://acme.test/rss", acme.Metadata["rssFeedUrl"]);
        Assert.Equal("id", acme.Metadata["rssItemId"]);
        Assert.Equal(FixedNow, acme.CollectedAt);
        Assert.Contains("ACME", acme.CompanyHints);

        var globex = items.Single(i => i.SourceName == "Globex IR");
        Assert.Contains("GLBX", globex.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), AcmeId, "Acme IR", "https://acme.test/rss");
        var reader = new FakeRssFeedReader { ["https://acme.test/rss"] = [Item("News", "https://acme.test/n1")] };
        var context = new CollectionContext([Company(AcmeId, "Acme Corp", ticker: null)], [feed]);

        var items = (await CreateCollector(reader).CollectAsync(context, CancellationToken.None)).ToList();

        var item = Assert.Single(items);
        Assert.Equal(["Acme Corp"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_DuplicateItemsBySameLink_AreDeduped()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), AcmeId, "Acme IR", "https://acme.test/rss");
        var reader = new FakeRssFeedReader
        {
            ["https://acme.test/rss"] =
            [
                Item("First", "https://acme.test/same"),
                Item("Second copy", "https://acme.test/same"),
            ],
        };
        var context = new CollectionContext([Company(AcmeId, "Acme Corp", "ACME")], [feed]);

        var items = (await CreateCollector(reader).CollectAsync(context, CancellationToken.None)).ToList();

        var item = Assert.Single(items);
        Assert.Equal("First", item.Title);
    }

    [Fact]
    public async Task CollectAsync_BlankTitleItem_IsSkipped()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), AcmeId, "Acme IR", "https://acme.test/rss");
        var reader = new FakeRssFeedReader
        {
            ["https://acme.test/rss"] =
            [
                Item("   ", "https://acme.test/blank"),
                Item("Real", "https://acme.test/real"),
            ],
        };
        var context = new CollectionContext([Company(AcmeId, "Acme Corp", "ACME")], [feed]);

        var items = (await CreateCollector(reader).CollectAsync(context, CancellationToken.None)).ToList();

        var item = Assert.Single(items);
        Assert.Equal("Real", item.Title);
    }

    [Fact]
    public async Task CollectAsync_NoRssFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var nonRss = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), AcmeId, "Acme Atom", "https://acme.test/atom",
            feedType: "atom");
        var reader = new FakeRssFeedReader();
        var context = new CollectionContext([Company(AcmeId, "Acme Corp", "ACME")], [nonRss]);

        var items = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(items);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task CollectAsync_RssFeedTypeIsCaseInsensitive()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), AcmeId, "Acme IR", "https://acme.test/rss",
            feedType: "RSS");
        var reader = new FakeRssFeedReader { ["https://acme.test/rss"] = [Item("News", "https://acme.test/n1")] };
        var context = new CollectionContext([Company(AcmeId, "Acme Corp", "ACME")], [feed]);

        var items = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(1, reader.ReadCount);
    }

    private sealed class FakeRssFeedReader : IRssFeedReader
    {
        private readonly Dictionary<string, IReadOnlyList<RssFeedItem>> _byUrl = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public IReadOnlyList<RssFeedItem> this[string url]
        {
            set => _byUrl[url] = value;
        }

        public Task<IReadOnlyList<RssFeedItem>> ReadAsync(string feedUrl, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(_byUrl.TryGetValue(feedUrl, out var items) ? items : []);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
