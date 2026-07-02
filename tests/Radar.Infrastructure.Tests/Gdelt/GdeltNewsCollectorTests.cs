using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Gdelt;

namespace Radar.Infrastructure.Tests.Gdelt;

public sealed class GdeltNewsCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgysId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string MrcyPhrase = "Mercury Systems";
    private const string MrcyToken = "query=Mercury Systems&ticker=MRCY";

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

    private static CompanySourceFeed Feed(
        Guid id, Guid companyId, string name, string url, string feedType = "news") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static GdeltArticleItem Article(
        string url,
        string title,
        string domain = "finance.yahoo.com",
        DateTimeOffset? seenDate = null,
        string language = "English",
        string sourceCountry = "United States") =>
        new(
            Url: url,
            Title: title,
            Domain: domain,
            SeenDate: seenDate ?? new DateTimeOffset(2026, 6, 27, 12, 30, 0, TimeSpan.Zero),
            Language: language,
            SourceCountry: sourceCountry);

    private static GdeltNewsCollector CreateCollector(
        FakeGdeltNewsReader reader, GdeltCollectorOptions? options = null) =>
        new(
            reader,
            NullLogger<GdeltNewsCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new GdeltCollectorOptions { InterRequestDelay = TimeSpan.Zero });

    [Fact]
    public async Task CollectAsync_MapsArticlesToNewsEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeGdeltNewsReader
        {
            [MrcyPhrase] =
            [
                Article(
                    "https://finance.yahoo.com/news/mrcy-defense",
                    "Mercury Systems , Inc . ( MRCY ): Among The Best Mid Cap Defense Stocks"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.NewsArticle, item.SourceType);
        Assert.Equal("Mercury — News", item.SourceName);
        Assert.Equal("https://finance.yahoo.com/news/mrcy-defense", item.SourceUrl);

        // PublishedAt is the article seendate parsed as UTC; CollectedAt is the TimeProvider now.
        Assert.Equal(new DateTimeOffset(2026, 6, 27, 12, 30, 0, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal(TimeSpan.Zero, item.PublishedAt!.Value.Offset);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + Medium quality.
        Assert.Equal("Medium", item.Metadata["quality"]);
        Assert.Equal("https://finance.yahoo.com/news/mrcy-defense", item.Metadata["url"]);
        Assert.Equal("finance.yahoo.com", item.Metadata["domain"]);
        Assert.Equal("2026-06-27T12:30:00Z", item.Metadata["seendate"]);
        Assert.Equal("English", item.Metadata["language"]);
        Assert.Equal("United States", item.Metadata["sourcecountry"]);
        Assert.Equal(MrcyToken, item.Metadata["gdeltFeedUrl"]);

        // url + title appear in the hashed RawText so distinct articles never collide.
        Assert.Contains("https://finance.yahoo.com/news/mrcy-defense", item.RawText);
        Assert.Contains("Mercury Systems", item.RawText);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["MRCY"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_DropsOffTopicArticleReferencingNeitherNameNorTicker()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — News", MrcyToken);

        // The verified MASSPHOTON false positive: matched the word "Mercury" loosely but references neither
        // the company name "Mercury Systems" nor the ticker MRCY — it must be dropped (provenance guard).
        var reader = new FakeGdeltNewsReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/keep", "Mercury Systems wins radar award"),
                Article(
                    "https://manilatimes.net/masphoton",
                    "MASSPHOTON Launches Advanced Mercury Water Disinfection System"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("https://ok.example/keep", item.SourceUrl);
    }

    [Fact]
    public async Task CollectAsync_SpacedTickerTitle_MatchesAndIsKept()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — News", MrcyToken);

        // The title references neither the phrase nor an un-spaced ticker, but a whitespace-normalised
        // "( MRCY )" contains the MRCY ticker token, so it is kept.
        var reader = new FakeGdeltNewsReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/ticker", "Defense sector movers today : ( MRCY ) climbs"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_DedupesByUrlWithinFeed()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeGdeltNewsReader
        {
            [MrcyPhrase] =
            [
                Article("https://dup.example/a", "Mercury Systems earnings beat"),
                Article("https://dup.example/a", "Mercury Systems earnings beat"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxRecordsPerCompany()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeGdeltNewsReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/1", "Mercury Systems news 1"),
                Article("https://ok.example/2", "Mercury Systems news 2"),
                Article("https://ok.example/3", "Mercury Systems news 3"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new GdeltCollectorOptions { MaxRecordsPerCompany = 2, InterRequestDelay = TimeSpan.Zero };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        Assert.Equal(2, result.Evidence.Count);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — News", "not-a-valid-token");
        var reader = new FakeGdeltNewsReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(0, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Mercury — News", failure.SourceName);
    }

    [Fact]
    public async Task CollectAsync_RateLimitedRead_DegradesToSourceFailureWithNoEvidence()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeGdeltNewsReader();
        reader.SetFailure(MrcyPhrase, GdeltReadOutcome.RateLimited, "HTTP 429 (rate limited)");

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(0, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("HTTP 429 (rate limited)", failure.Reason);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithNoCoverage_ProducesNoEvidenceWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeGdeltNewsReader { [MrcyPhrase] = [] };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
    }

    [Fact]
    public async Task CollectAsync_NoNewsFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var nonNews = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury RSS", "https://mrcy.test/rss",
            feedType: "rss");
        var reader = new FakeGdeltNewsReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [nonNews]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — News", "query=Mercury Systems");
        var reader = new FakeGdeltNewsReader
        {
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems update")],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal(["Mercury Systems"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_ProcessesFeedsSequentiallyInDeterministicOrder()
    {
        const string agysPhrase = "Agilysys";
        var agysToken = "query=Agilysys&ticker=AGYS";

        // MrcyId < AgysId, so FeedsOfType orders Mercury's feed first regardless of list order.
        var agysFeed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), AgysId, "Agilysys — News", agysToken);
        var mrcyFeed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeGdeltNewsReader
        {
            [MrcyPhrase] = [Article("https://ok.example/m", "Mercury Systems moves")],
        };
        // Agilysys read fails, exercising the failed-count path alongside a successful feed.
        reader.SetFailure(agysPhrase, GdeltReadOutcome.HttpError, "HTTP 500");

        var logger = new CapturingLogger<GdeltNewsCollector>();
        var collector = new GdeltNewsCollector(
            reader, logger, new FixedTimeProvider(FixedNow),
            new GdeltCollectorOptions { InterRequestDelay = TimeSpan.Zero });

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(AgysId, "Agilysys", "AGYS")],
            [agysFeed, mrcyFeed]);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Mercury — News", item.SourceName);

        // The reader saw the phrases strictly sequentially in the deterministic (CompanyId, Id) order.
        Assert.Equal([MrcyPhrase, agysPhrase], reader.QueryPhrasesInOrder);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Agilysys — News", failure.SourceName);
        Assert.Equal(agysToken, failure.SourceUrl);
        Assert.Equal("HTTP 500", failure.Reason);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Agilysys", warning.Message);
    }

    private sealed class FakeGdeltNewsReader : IGdeltNewsReader
    {
        private readonly Dictionary<string, GdeltReadResult> _byPhrase = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public GdeltNewsQuery? LastQuery { get; private set; }

        public List<string> QueryPhrasesInOrder { get; } = [];

        public IReadOnlyList<GdeltArticleItem> this[string phrase]
        {
            set => _byPhrase[phrase] = GdeltReadResult.Success(value);
        }

        public void SetFailure(string phrase, GdeltReadOutcome outcome, string detail) =>
            _byPhrase[phrase] = GdeltReadResult.Failure(outcome, detail);

        public Task<GdeltReadResult> ReadAsync(GdeltNewsQuery query, CancellationToken ct)
        {
            ReadCount++;
            LastQuery = query;
            QueryPhrasesInOrder.Add(query.QueryPhrase);
            return Task.FromResult(
                _byPhrase.TryGetValue(query.QueryPhrase, out var result)
                    ? result
                    : GdeltReadResult.Success([]));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
