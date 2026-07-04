using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.News;

namespace Radar.Infrastructure.Tests.News;

public sealed class NewsAttentionCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RklbId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string MrcyPhrase = "Mercury Systems";
    private const string MrcyToken = "query=Mercury Systems&ticker=MRCY";

    private const string RklbPhrase = "Rocket Lab";
    private const string RklbToken = "query=Rocket Lab&ticker=RKLB";

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
        Guid id, Guid companyId, string name, string url, string feedType = "newssearch") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static NewsArticleItem Article(
        string url,
        string title,
        string sourceName = "Reuters",
        DateTimeOffset? publishedAt = null) =>
        new(
            Url: url,
            Title: title,
            SourceName: sourceName,
            PublishedAt: publishedAt ?? new DateTimeOffset(2026, 6, 27, 12, 30, 0, TimeSpan.Zero));

    private static NewsAttentionCollector CreateCollector(
        FakeNewsSearchReader reader, NewsCollectorOptions? options = null) =>
        new(
            reader,
            NullLogger<NewsAttentionCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero });

    [Fact]
    public async Task CollectAsync_MapsArticlesToNewsEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article(
                    "https://news.google.com/rss/articles/mrcy-defense",
                    "Mercury Systems, Inc. (MRCY): Among The Best Mid Cap Defense Stocks - Yahoo Finance",
                    sourceName: "Yahoo Finance"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.NewsArticle, item.SourceType);
        // SourceName is the article's real outlet (the breadth input), not the per-company feed name.
        Assert.Equal("Yahoo Finance", item.SourceName);
        Assert.Equal("https://news.google.com/rss/articles/mrcy-defense", item.SourceUrl);

        // Title is stored as-is (the " - Publisher" suffix is kept for provenance).
        Assert.Contains("- Yahoo Finance", item.Title);

        // PublishedAt is the article pubDate parsed as UTC; CollectedAt is the TimeProvider now.
        Assert.Equal(new DateTimeOffset(2026, 6, 27, 12, 30, 0, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal(TimeSpan.Zero, item.PublishedAt!.Value.Offset);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + Medium quality.
        Assert.Equal("Medium", item.Metadata["quality"]);
        Assert.Equal("https://news.google.com/rss/articles/mrcy-defense", item.Metadata["url"]);
        Assert.Equal("Yahoo Finance", item.Metadata["publisher"]);
        // The per-company feed attribution is retained in metadata now that SourceName is the outlet.
        Assert.Equal("Mercury — News", item.Metadata["feedName"]);
        Assert.Equal("2026-06-27T12:30:00Z", item.Metadata["pubDate"]);
        Assert.Equal(MrcyToken, item.Metadata["newsSearchFeedUrl"]);

        // NewsArticleItem has no language/country field, so those metadata keys are not invented.
        Assert.False(item.Metadata.ContainsKey("language"));
        Assert.False(item.Metadata.ContainsKey("sourcecountry"));

        // url + title appear in the hashed RawText so distinct articles never collide.
        Assert.Contains("https://news.google.com/rss/articles/mrcy-defense", item.RawText);
        Assert.Contains("Mercury Systems", item.RawText);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["MRCY"], item.CompanyHints);

        // No advice language.
        AssertNoAdviceLanguage(item);
    }

    [Fact]
    public async Task CollectAsync_DistinctPublishers_ProduceDistinctSourceNames()
    {
        // Breadth becomes real: three distinct outlets covering the same company yield three distinct
        // evidence SourceNames, so the formula's Distinct(SourceName) counts three outlets.
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000d"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/1", "Mercury Systems wins radar deal - Reuters", sourceName: "Reuters"),
                Article("https://ok.example/2", "Mercury Systems beats estimates - Yahoo Finance",
                    sourceName: "Yahoo Finance"),
                Article("https://ok.example/3", "Mercury Systems upgraded - MarketBeat", sourceName: "MarketBeat"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Equal(3, result.Evidence.Count);
        Assert.Equal(
            new HashSet<string> { "Reuters", "Yahoo Finance", "MarketBeat" },
            result.Evidence.Select(e => e.SourceName).ToHashSet());
    }

    [Fact]
    public async Task CollectAsync_SamePublisherRepeated_KeepsSameSourceName()
    {
        // Outlet dedupe holds: three distinct-URL Reuters articles all carry SourceName "Reuters", so the
        // formula's Distinct(SourceName) counts a single outlet.
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000e"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/1", "Mercury Systems news 1 - Reuters", sourceName: "Reuters"),
                Article("https://ok.example/2", "Mercury Systems news 2 - Reuters", sourceName: "Reuters"),
                Article("https://ok.example/3", "Mercury Systems news 3 - Reuters", sourceName: "Reuters"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Equal(3, result.Evidence.Count);
        Assert.All(result.Evidence, e => Assert.Equal("Reuters", e.SourceName));
    }

    [Fact]
    public async Task CollectAsync_BlankPublisher_FallsBackToFeedName()
    {
        // An unattributable article (blank publisher) still carries a readable label — the feed name — while
        // metadata["publisher"] preserves the (blank) parsed value. Breadth is unaffected: the formula skips
        // blank names, and the feed-name bucket is per-company constant.
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000f"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/blank", "Mercury Systems update", sourceName: "")],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Mercury — News", item.SourceName);
        Assert.Equal("", item.Metadata["publisher"]);
        Assert.Equal("Mercury — News", item.Metadata["feedName"]);
    }

    [Fact]
    public async Task CollectAsync_DropsOffTopicArticleReferencingNeitherNameNorTicker()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — News", MrcyToken);

        // The verified MASSPHOTON false positive: matched the word "Mercury" loosely but references neither
        // the company name "Mercury Systems" nor the ticker MRCY — it must be dropped (provenance guard).
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/keep", "Mercury Systems wins radar award - Reuters"),
                Article(
                    "https://manilatimes.net/masphoton",
                    "MASSPHOTON Launches Advanced Mercury Water Disinfection System - Manila Times"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("https://ok.example/keep", item.SourceUrl);
    }

    [Fact]
    public async Task CollectAsync_PublisherSuffixContainingTicker_DoesNotProduceFalseMatch()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — News", MrcyToken);

        // The headline itself references neither the phrase nor the ticker; only the publisher suffix contains
        // "MRCY". Stripping the suffix before the check prevents that false match, so the article is dropped.
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/false", "Defense sector movers today - MRCY Wire"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_SpacedSuffixedTitle_MatchesAfterStripAndNormalise()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), RklbId, "Rocket Lab — News", RklbToken);

        // The title has spaced-out punctuation and a " - Publisher" suffix; after suffix strip + whitespace
        // normalisation "Rocket Lab USA , Inc . ( RKLB )" matches both the "Rocket Lab" phrase and "RKLB".
        var reader = new FakeNewsSearchReader
        {
            [RklbPhrase] =
            [
                Article(
                    "https://ok.example/rklb",
                    "Rocket Lab USA , Inc . ( RKLB ) - Reuters",
                    sourceName: "Reuters"),
            ],
        };

        var context = new CollectionContext([Company(RklbId, "Rocket Lab", "RKLB")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_DedupesByUrlWithinFeed()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://dup.example/a", "Mercury Systems earnings beat - Reuters"),
                Article("https://dup.example/a", "Mercury Systems earnings beat - Reuters"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxRecordsPerCompany()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/1", "Mercury Systems news 1 - Reuters"),
                Article("https://ok.example/2", "Mercury Systems news 2 - Reuters"),
                Article("https://ok.example/3", "Mercury Systems news 3 - Reuters"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new NewsCollectorOptions { MaxRecordsPerCompany = 2, InterRequestDelay = TimeSpan.Zero };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        Assert.Equal(2, result.Evidence.Count);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury — News", "not-a-valid-token");
        var reader = new FakeNewsSearchReader();
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
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader();
        reader.SetFailure(MrcyPhrase, NewsSearchReadOutcome.RateLimited, "HTTP 429 (rate limited)");

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
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader { [MrcyPhrase] = [] };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
    }

    [Fact]
    public async Task CollectAsync_NoNewsSearchFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var nonNews = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury RSS", "https://mrcy.test/rss",
            feedType: "rss");
        var reader = new FakeNewsSearchReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [nonNews]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000b"), MrcyId, "Mercury — News", "query=Mercury Systems");
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems update - Reuters")],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal(["Mercury Systems"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_ProcessesFeedsSequentiallyInDeterministicOrder()
    {
        // MrcyId < RklbId, so FeedsOfType orders Mercury's feed first regardless of list order.
        var rklbFeed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), RklbId, "Rocket Lab — News", RklbToken);
        var mrcyFeed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — News", MrcyToken);

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/m", "Mercury Systems moves - Reuters")],
        };
        // Rocket Lab read fails, exercising the failed-count path alongside a successful feed.
        reader.SetFailure(RklbPhrase, NewsSearchReadOutcome.HttpError, "HTTP 500");

        var logger = new CapturingLogger<NewsAttentionCollector>();
        var collector = new NewsAttentionCollector(
            reader, logger, new FixedTimeProvider(FixedNow),
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero });

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(RklbId, "Rocket Lab", "RKLB")],
            [rklbFeed, mrcyFeed]);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        // SourceName is now the article's outlet; the feed name is retained in metadata.
        Assert.Equal("Reuters", item.SourceName);
        Assert.Equal("Mercury — News", item.Metadata["feedName"]);

        // The reader saw the phrases strictly sequentially in the deterministic (CompanyId, Id) order.
        Assert.Equal([MrcyPhrase, RklbPhrase], reader.QueryPhrasesInOrder);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Rocket Lab — News", failure.SourceName);
        Assert.Equal(RklbToken, failure.SourceUrl);
        Assert.Equal("HTTP 500", failure.Reason);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Rocket Lab", warning.Message);
    }

    [Fact]
    public async Task CollectAsync_CancelledToken_Throws()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000c"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems update - Reuters")],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateCollector(reader).CollectAsync(context, cts.Token));
    }

    private static void AssertNoAdviceLanguage(CollectedEvidence item)
    {
        string[] banned = ["buy", "sell", "guaranteed upside", "safe bet"];
        var haystack = $"{item.Title} {item.RawText}";
        foreach (var word in banned)
        {
            Assert.DoesNotContain(word, haystack, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeNewsSearchReader : INewsSearchReader
    {
        private readonly Dictionary<string, NewsSearchReadResult> _byPhrase = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public NewsSearchQuery? LastQuery { get; private set; }

        public List<string> QueryPhrasesInOrder { get; } = [];

        public IReadOnlyList<NewsArticleItem> this[string phrase]
        {
            set => _byPhrase[phrase] = NewsSearchReadResult.Success(value);
        }

        public void SetFailure(string phrase, NewsSearchReadOutcome outcome, string detail) =>
            _byPhrase[phrase] = NewsSearchReadResult.Failure(outcome, detail);

        public Task<NewsSearchReadResult> ReadAsync(NewsSearchQuery query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            LastQuery = query;
            QueryPhrasesInOrder.Add(query.QueryPhrase);
            return Task.FromResult(
                _byPhrase.TryGetValue(query.QueryPhrase, out var result)
                    ? result
                    : NewsSearchReadResult.Success([]));
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
