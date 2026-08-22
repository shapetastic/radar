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

    // -------------------------------------------------------------------------------------------------
    // Spec 169 / AD-16: per-company collection COVERAGE. This is what turns a publisher count of zero into
    // a valid zero rather than an unobserved window, so each rule is asserted individually.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CollectorName_EqualsTheFeedTypeItSelects_WhichIsWhatTheHealthStampJoinsOn()
    {
        // CollectionPass stamps CollectionHealthMismatch onto a collector's coverage rows by matching the
        // collector's NAME against CollectionHealthWarning.FeedType — the only join available, since
        // IEvidenceCollector declares no feed type. If the two ever diverged, a lost-feed warning would stop
        // marking newssearch coverage and the AD-16 evaluator would silently OVER-certify a window. Pinned
        // here: this collector selects "newssearch" feeds and is named "newssearch".
        Assert.Equal("newssearch", NewsAttentionCollector.Name);

        var typed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"), MrcyId, "Mercury — News", MrcyToken,
            feedType: NewsAttentionCollector.Name);
        var otherType = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000002"), MrcyId, "Mercury — RSS", MrcyToken,
            feedType: "rss");

        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems update - Reuters")],
        };
        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY")], [typed, otherType]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        // Exactly one feed was checked: the one whose FeedType matches this collector's name.
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, Assert.Single(result.CompanyCoverage!).ExpectedFeedCount);
    }

    [Fact]
    public async Task CollectAsync_RecordsCoverageForEveryCompany_IncludingOnesWithNoFeed()
    {
        var feed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems update - Reuters")],
        };

        // RKLB is in the universe but has NO newssearch feed configured.
        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(RklbId, "Rocket Lab", "RKLB")],
            [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var coverage = Assert.IsAssignableFrom<IReadOnlyList<CollectorCompanyCoverage>>(result.CompanyCoverage);
        Assert.Equal(2, coverage.Count);

        // Ordered by CompanyId (AD-3), so two runs record byte-identical coverage.
        Assert.Equal(coverage.OrderBy(c => c.CompanyId).Select(c => c.CompanyId), coverage.Select(c => c.CompanyId));

        var mrcy = Assert.Single(coverage, c => c.CompanyId == MrcyId);
        Assert.Equal(1, mrcy.ExpectedFeedCount);
        Assert.Equal(1, mrcy.SuccessfulFeedCount);
        Assert.False(mrcy.HitEffectiveResultLimit);
        Assert.Empty(mrcy.Issues);

        // A company Radar never asked about is RECORDED as MissingFeed, never silently absent — an absent row
        // and a clean row must not be the same thing.
        var rklb = Assert.Single(coverage, c => c.CompanyId == RklbId);
        Assert.Equal(0, rklb.ExpectedFeedCount);
        Assert.Equal([CollectionCoverageIssues.MissingFeed], rklb.Issues);
    }

    [Fact]
    public async Task CollectAsync_FeedFailure_RecordsSourceFailureCoverage()
    {
        var feed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader();
        reader.SetFailure(MrcyPhrase, NewsSearchReadOutcome.HttpError, "HTTP 500");

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.Equal(1, coverage.ExpectedFeedCount);
        Assert.Equal(0, coverage.SuccessfulFeedCount);
        Assert.Equal([CollectionCoverageIssues.SourceFailure], coverage.Issues);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_CountsAsASourceFailureForCoverage()
    {
        var feed = Feed(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003"), MrcyId, "Mercury — News", "not-a-token");

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(new FakeNewsSearchReader())
            .CollectAsync(context, CancellationToken.None);

        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.Equal(1, coverage.ExpectedFeedCount);
        Assert.Equal(0, coverage.SuccessfulFeedCount);
        Assert.Equal([CollectionCoverageIssues.SourceFailure], coverage.Issues);
    }

    [Fact]
    public async Task CollectAsync_RawResultCountAtTheLimit_IsCensored_EvenWhenRelevanceFilteringKeepsFewer()
    {
        var feed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004"), MrcyId, "Mercury — News", MrcyToken);

        // THREE raw articles == MaxRecordsPerCompany, but only ONE survives the client-side relevance filter.
        // Equality with the EFFECTIVE limit is what censoring means: the source stopped at the ceiling Radar
        // asked for, so anything beyond it is unobserved regardless of how many items were kept.
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/1", "Mercury Systems wins a contract - Reuters"),
                Article("https://ok.example/2", "Something entirely unrelated - Reuters"),
                Article("https://ok.example/3", "Another unrelated headline - Reuters"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, MaxRecordsPerCompany = 3 });

        var result = await collector.CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence); // only one relevant article was kept …
        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.True(coverage.HitEffectiveResultLimit); // … and the window is STILL not provably complete.
        Assert.Equal(1, coverage.SuccessfulFeedCount);
        Assert.Equal([CollectionCoverageIssues.ResultLimitReached], coverage.Issues);
    }

    [Fact]
    public async Task CollectAsync_RawResultCountBelowTheLimit_IsNotCensored()
    {
        var feed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000005"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems wins a contract - Reuters")],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, MaxRecordsPerCompany = 3 });

        var coverage = Assert.Single(
            (await collector.CollectAsync(context, CancellationToken.None)).CompanyCoverage!);
        Assert.False(coverage.HitEffectiveResultLimit);
        Assert.Empty(coverage.Issues);
    }

    [Fact]
    public async Task CollectAsync_CensoringUsesTheEFFECTIVEClampedLimit_NotTheUnclampedConfigValue()
    {
        var feed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000006"), MrcyId, "Mercury — News", MrcyToken);

        // A configured 0 clamps UP to the API minimum of 1 — which is what BuildQuery sends — so a single
        // returned article REACHES the effective limit. Testing against the unclamped 0 would call every
        // non-empty result censored, and testing against a hard-coded 100 would call none of them censored.
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems wins a contract - Reuters")],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, MaxRecordsPerCompany = 0 });

        var result = await collector.CollectAsync(context, CancellationToken.None);

        Assert.Equal(1, reader.LastQuery!.MaxRecords);
        Assert.True(Assert.Single(result.CompanyCoverage!).HitEffectiveResultLimit);
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 177 §3: the observation sidecar. One row per SURVIVING article; the evidence mapping stays
    // byte-identical; the collector never touches a filesystem store (it only accumulates rows).
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CollectAsync_EmitsOneObservationPerSurvivingArticle_WithFullProvenance()
    {
        var feedId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
        var feed = Feed(feedId, MrcyId, "Mercury — News", MrcyToken);

        var retrievedAt = new DateTimeOffset(2026, 6, 27, 11, 59, 0, TimeSpan.Zero);
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                new NewsArticleItem(
                    Url: "https://news.google.com/rss/articles/mrcy-1",
                    Title: "Mercury Systems wins radar award - Reuters",
                    SourceName: "Reuters",
                    PublishedAt: new DateTimeOffset(2026, 6, 27, 9, 0, 0, TimeSpan.Zero),
                    DescriptionRaw: "<a href=\"x\">Mercury Systems wins radar award</a>",
                    DescriptionText: "Mercury Systems wins radar award",
                    DescriptionTruncated: false,
                    PublisherSiteUrl: "https://reuters.com",
                    RetrievedAt: retrievedAt),
                // Off-topic: dropped by the relevance filter — it must NOT be archived against the company.
                Article("https://off.example/x", "Something entirely unrelated - Reuters"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var observation = Assert.Single(result.Observations!);
        Assert.Equal(MrcyId, observation.CompanyId);
        Assert.Equal("MRCY", observation.Ticker);
        Assert.Equal(NewsAttentionCollector.Name, observation.Collector);
        Assert.Equal(MrcyPhrase, observation.QueryPhrase);
        Assert.Equal(feedId, observation.FeedId);
        Assert.Equal("Mercury — News", observation.FeedName);
        Assert.Equal("https://news.google.com/rss/articles/mrcy-1", observation.GoogleLandingUrl);
        Assert.Equal("Reuters", observation.Publisher);
        Assert.Equal("https://reuters.com", observation.PublisherSiteUrl);
        Assert.Equal("Mercury Systems wins radar award - Reuters", observation.Headline);
        Assert.Equal("<a href=\"x\">Mercury Systems wins radar award</a>", observation.DescriptionRaw);
        Assert.Equal("Mercury Systems wins radar award", observation.DescriptionText);
        Assert.False(observation.DescriptionTruncated);
        Assert.Equal(new DateTimeOffset(2026, 6, 27, 9, 0, 0, TimeSpan.Zero), observation.PublishedAtUtc);
        Assert.Equal(retrievedAt, observation.RetrievedAtUtc);
    }

    [Fact]
    public async Task CollectAsync_ObservationSidecar_RespectsDedupeAndPerFeedCap()
    {
        var feed = Feed(Guid.Parse("dddddddd-0000-0000-0000-000000000002"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader
        {
            [MrcyPhrase] =
            [
                Article("https://dup.example/a", "Mercury Systems earnings beat - Reuters"),
                Article("https://dup.example/a", "Mercury Systems earnings beat - Reuters"), // URL dupe
                Article("https://ok.example/2", "Mercury Systems news 2 - Reuters"),
                Article("https://ok.example/3", "Mercury Systems news 3 - Reuters"),         // over the cap
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new NewsCollectorOptions { MaxRecordsPerCompany = 2, InterRequestDelay = TimeSpan.Zero };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        // The sidecar mirrors the SURVIVING set exactly: same count, same URLs, same order as the evidence.
        Assert.Equal(2, result.Observations!.Count);
        Assert.Equal(
            result.Evidence.Select(e => e.SourceUrl),
            result.Observations!.Select(o => o.GoogleLandingUrl));
    }

    [Fact]
    public async Task CollectAsync_DescriptionPayload_LeavesEvidenceByteIdentical()
    {
        // The load-bearing spec-177 compatibility test: two articles differing ONLY in the (new)
        // description/source-url/retrieval fields must map to IDENTICAL CollectedEvidence — Title, RawText,
        // metadata, hints, everything — because those fields feed only the observation sidecar, never
        // evidence identity (spec 145) or the mapper's Title+RawText ContentHash.
        var feed = Feed(Guid.Parse("dddddddd-0000-0000-0000-000000000003"), MrcyId, "Mercury — News", MrcyToken);
        var publishedAt = new DateTimeOffset(2026, 6, 27, 9, 0, 0, TimeSpan.Zero);

        var bare = new NewsArticleItem(
            Url: "https://news.google.com/rss/articles/same",
            Title: "Mercury Systems wins radar award - Reuters",
            SourceName: "Reuters",
            PublishedAt: publishedAt);
        var enriched = bare with
        {
            DescriptionRaw = "<b>rich description</b>",
            DescriptionText = "rich description",
            DescriptionTruncated = true,
            PublisherSiteUrl = "https://reuters.com",
            RetrievedAt = new DateTimeOffset(2026, 6, 27, 11, 0, 0, TimeSpan.Zero),
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var readerBare = new FakeNewsSearchReader { [MrcyPhrase] = [bare] };
        var readerEnriched = new FakeNewsSearchReader { [MrcyPhrase] = [enriched] };

        var evidenceBare = Assert.Single(
            (await CreateCollector(readerBare).CollectAsync(context, CancellationToken.None)).Evidence);
        var evidenceEnriched = Assert.Single(
            (await CreateCollector(readerEnriched).CollectAsync(context, CancellationToken.None)).Evidence);

        Assert.Equal(evidenceBare.SourceType, evidenceEnriched.SourceType);
        Assert.Equal(evidenceBare.SourceName, evidenceEnriched.SourceName);
        Assert.Equal(evidenceBare.SourceUrl, evidenceEnriched.SourceUrl);
        Assert.Equal(evidenceBare.Title, evidenceEnriched.Title);
        Assert.Equal(evidenceBare.RawText, evidenceEnriched.RawText);
        Assert.Equal(evidenceBare.PublishedAt, evidenceEnriched.PublishedAt);
        Assert.Equal(evidenceBare.CollectedAt, evidenceEnriched.CollectedAt);
        Assert.Equal(evidenceBare.CompanyHints, evidenceEnriched.CompanyHints);
        Assert.Equal(
            evidenceBare.Metadata.OrderBy(kvp => kvp.Key, StringComparer.Ordinal),
            evidenceEnriched.Metadata.OrderBy(kvp => kvp.Key, StringComparer.Ordinal));
        // And the metadata bag gained NO new key: the description is not smuggled into evidence.
        Assert.DoesNotContain(evidenceEnriched.Metadata.Keys, k => k.Contains("description", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CollectAsync_HandBuiltArticleWithDefaultRetrievedAt_FallsBackToCollectorClock()
    {
        var feed = Feed(Guid.Parse("dddddddd-0000-0000-0000-000000000004"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader
        {
            // The Article() helper never sets RetrievedAt, so this exercises the default(DateTimeOffset)
            // fallback: an observation must never carry the meaningless default instant.
            [MrcyPhrase] = [Article("https://ok.example/1", "Mercury Systems update - Reuters")],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Equal(FixedNow, Assert.Single(result.Observations!).RetrievedAtUtc);
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
