using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.News;
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
        FakeNewsSearchReader reader,
        NewsCollectorOptions? options = null,
        INewsObservationCompanyHistory? history = null,
        DateTimeOffset? now = null) =>
        new(
            reader,
            NullLogger<NewsAttentionCollector>.Instance,
            new FixedTimeProvider(now ?? FixedNow),
            // Every pre-198 test constructs the options without naming RecencyWindowDays, which would pick
            // up the shipped default of 7 and change what those tests exercise. The DEFAULT for this helper
            // is therefore the disabled window, so the spec-198 tests below are the only ones that opt in —
            // exactly mirroring "a window of 0 reproduces pre-198 behaviour".
            options ?? new NewsCollectorOptions
            {
                InterRequestDelay = TimeSpan.Zero,
                RecencyWindowDays = 0,
            },
            history);

    /// <summary>
    /// A spec-198 §2 history seam over a fixed set of company ids, counting how many times it is asked. The
    /// collector must resolve it ONCE per collection pass, not once per feed.
    /// </summary>
    private sealed class FakeObservationHistory(params Guid[] companies) : INewsObservationCompanyHistory
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlySet<Guid>> GetCompaniesWithObservationsAsync(CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<IReadOnlySet<Guid>>(companies.ToHashSet());
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Spec 198 §2: the recency window per feed, and the first-collection exemption.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CollectAsync_CompanyWithPriorObservations_IssuesTheWindowedQuery()
    {
        var feed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader();
        var history = new FakeObservationHistory(MrcyId);

        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 },
            history);

        var result = await collector.CollectAsync(
            new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]),
            CancellationToken.None);

        Assert.Equal(7, Assert.Single(reader.QueriesInOrder).RecencyWindowDays);

        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.Equal(7, coverage.RecencyWindowDays);
        Assert.Equal(0, coverage.UnfilteredFirstCollectionFeedCount);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithNoPriorObservation_IssuesTheUnfilteredFirstCollectionQuery()
    {
        // THE seeding guarantee: the unfiltered query is the only way a newly seeded company acquires back
        // history, so a company the archive has never seen must still get it.
        var feed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000002"), RklbId, "Rocket Lab — News", RklbToken);
        var reader = new FakeNewsSearchReader();

        // The archive holds observations for a DIFFERENT company only.
        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 },
            new FakeObservationHistory(MrcyId));

        var result = await collector.CollectAsync(
            new CollectionContext([Company(RklbId, "Rocket Lab", "RKLB")], [feed]),
            CancellationToken.None);

        Assert.Equal(0, Assert.Single(reader.QueriesInOrder).RecencyWindowDays);

        var coverage = Assert.Single(result.CompanyCoverage!);
        // The CONFIGURED window is recorded even though this feed did not apply it — the two facts are
        // different, and the second is what the count reports.
        Assert.Equal(7, coverage.RecencyWindowDays);
        Assert.Equal(1, coverage.UnfilteredFirstCollectionFeedCount);
    }

    [Fact]
    public async Task CollectAsync_FirstCollectionDecision_ComesFromPersistedState_NotFromAClock()
    {
        // AD-3: the rule is "does the archive already hold an observation for this company", never "was the
        // last run recent enough". Proven by advancing the collector's TimeProvider by a year and asserting
        // the issued queries are IDENTICAL — a clock-derived rule would have re-widened the query.
        var mrcyFeed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000003"), MrcyId, "Mercury — News", MrcyToken);
        var rklbFeed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000004"), RklbId, "Rocket Lab — News", RklbToken);
        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(RklbId, "Rocket Lab", "RKLB")],
            [mrcyFeed, rklbFeed]);
        var options = new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 };

        var now = new FakeNewsSearchReader();
        await CreateCollector(now, options, new FakeObservationHistory(MrcyId)).CollectAsync(
            context, CancellationToken.None);

        var muchLater = new FakeNewsSearchReader();
        await CreateCollector(
                muchLater,
                options,
                new FakeObservationHistory(MrcyId),
                now: FixedNow.AddYears(1))
            .CollectAsync(context, CancellationToken.None);

        Assert.Equal(
            now.QueriesInOrder.Select(q => (q.QueryPhrase, q.RecencyWindowDays)),
            muchLater.QueriesInOrder.Select(q => (q.QueryPhrase, q.RecencyWindowDays)));

        // …and the split really is per company: Mercury windowed, Rocket Lab (no history) unfiltered.
        Assert.Equal(
            [(MrcyPhrase, 7), (RklbPhrase, 0)],
            now.QueriesInOrder.Select(q => (q.QueryPhrase, q.RecencyWindowDays)));
    }

    [Fact]
    public async Task CollectAsync_NoHistorySeamRegistered_IssuesEveryQueryUnfiltered()
    {
        // FAIL CLOSED TO "NO IMPROVEMENT": a composition that registers no history seam cannot establish
        // which companies are new, so it narrows nothing and behaves exactly as pre-198.
        var feed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000005"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader();

        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 },
            history: null);

        var result = await collector.CollectAsync(
            new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]),
            CancellationToken.None);

        Assert.Equal(0, Assert.Single(reader.QueriesInOrder).RecencyWindowDays);
        Assert.Equal(1, Assert.Single(result.CompanyCoverage!).UnfilteredFirstCollectionFeedCount);
    }

    [Fact]
    public async Task CollectAsync_WindowDisabled_IssuesUnfilteredQueries_AndCountsNoFirstCollection()
    {
        // With the filter configured OFF every query is unfiltered for an unrelated reason, so the recorded
        // window of 0 says it and the first-collection count stays honestly zero — counting these as first
        // collections would report an exemption that never applied.
        var feed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-000000000006"), MrcyId, "Mercury — News", MrcyToken);
        var reader = new FakeNewsSearchReader();

        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 0 },
            new FakeObservationHistory(MrcyId));

        var result = await collector.CollectAsync(
            new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]),
            CancellationToken.None);

        Assert.Equal(0, Assert.Single(reader.QueriesInOrder).RecencyWindowDays);

        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.Equal(0, coverage.RecencyWindowDays);
        Assert.Equal(0, coverage.UnfilteredFirstCollectionFeedCount);
    }

    [Fact]
    public async Task CollectAsync_ResolvesTheHistorySeamOncePerPass_NotOncePerFeed()
    {
        // The seam hydrates a whole archive index; asking it per feed would re-answer an identical question
        // dozens of times per run.
        var reader = new FakeNewsSearchReader();
        var history = new FakeObservationHistory(MrcyId);

        await CreateCollector(
                reader,
                new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 },
                history)
            .CollectAsync(
                new CollectionContext(
                    [Company(MrcyId, "Mercury Systems", "MRCY"), Company(RklbId, "Rocket Lab", "RKLB")],
                    [
                        Feed(Guid.Parse("cccccccc-0000-0000-0000-000000000007"), MrcyId, "M1", MrcyToken),
                        Feed(Guid.Parse("cccccccc-0000-0000-0000-000000000008"), MrcyId, "M2", MrcyToken),
                        Feed(Guid.Parse("cccccccc-0000-0000-0000-000000000009"), RklbId, "R1", RklbToken),
                    ]),
                CancellationToken.None);

        Assert.Equal(1, history.CallCount);
        Assert.Equal(3, reader.QueriesInOrder.Count);
    }

    [Fact]
    public async Task CollectAsync_RecordsTheSpec198Diagnostics_OnEveryRow_IncludingMissingFeedAndFailedRows()
    {
        // The spec-190 convention verbatim: `null` stays reserved for a collector that records none, so a
        // MissingFeed row and a failed-read row both carry the CONFIGURED window and an honest count.
        var rklbFeed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-00000000000a"), RklbId, "Rocket Lab — News", RklbToken);
        var reader = new FakeNewsSearchReader();
        reader.SetFailure(RklbPhrase, NewsSearchReadOutcome.HttpError, "HTTP 500");

        // Mercury holds observations but has NO feed (MissingFeed); Rocket Lab has a feed that fails.
        var collector = CreateCollector(
            reader,
            new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 },
            new FakeObservationHistory(MrcyId, RklbId));

        var result = await collector.CollectAsync(
            new CollectionContext(
                [Company(MrcyId, "Mercury Systems", "MRCY"), Company(RklbId, "Rocket Lab", "RKLB")],
                [rklbFeed]),
            CancellationToken.None);

        var rows = result.CompanyCoverage!;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(7, r.RecencyWindowDays));

        var missingFeed = rows.Single(r => r.CompanyId == MrcyId);
        Assert.Contains(CollectionCoverageIssues.MissingFeed, missingFeed.Issues);
        Assert.Equal(0, missingFeed.UnfilteredFirstCollectionFeedCount);

        // Rocket Lab HAS history, so its (failed) feed still issued the WINDOWED query — the count reports
        // the query shape Radar sent, not the read's outcome.
        var failed = rows.Single(r => r.CompanyId == RklbId);
        Assert.Contains(CollectionCoverageIssues.SourceFailure, failed.Issues);
        Assert.Equal(0, failed.UnfilteredFirstCollectionFeedCount);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_IssuesNoQuery_AndCountsNoFirstCollection()
    {
        // A malformed token never reaches the reader, so it is neither windowed nor an unfiltered first
        // collection: nothing was asked.
        var feed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-00000000000b"), RklbId, "Rocket Lab — News", "garbage");
        var reader = new FakeNewsSearchReader();

        var result = await CreateCollector(
                reader,
                new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 },
                new FakeObservationHistory())
            .CollectAsync(
                new CollectionContext([Company(RklbId, "Rocket Lab", "RKLB")], [feed]),
                CancellationToken.None);

        Assert.Empty(reader.QueriesInOrder);
        var coverage = Assert.Single(result.CompanyCoverage!);
        Assert.Equal(7, coverage.RecencyWindowDays);
        Assert.Equal(0, coverage.UnfilteredFirstCollectionFeedCount);
    }

    [Fact]
    public async Task CollectAsync_WindowedQuery_LeavesEveryDownstreamMechanicUnchanged()
    {
        // Spec 198 §5/§6: the window changes only WHAT THE PROVIDER IS ASKED FOR. Given an identical
        // response, the relevance filter, the URL dedupe, the per-feed cap, the evidence mapping and the
        // observation capture are byte-identical to the unfiltered arm.
        var feed = Feed(
            Guid.Parse("cccccccc-0000-0000-0000-00000000000c"), MrcyId, "Mercury — News", MrcyToken);
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        static FakeNewsSearchReader Reader() => new()
        {
            [MrcyPhrase] =
            [
                Article("https://ok.example/1", "Mercury Systems wins radar deal - Reuters"),
                Article("https://ok.example/1", "Mercury Systems wins radar deal - Reuters"), // dupe url
                Article("https://ok.example/2", "Unrelated company story - Reuters"),          // off-topic
                Article("https://ok.example/3", "Mercury Systems beats estimates - Yahoo Finance",
                    sourceName: "Yahoo Finance"),
            ],
        };

        var windowed = await CreateCollector(
                Reader(),
                new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 7 },
                new FakeObservationHistory(MrcyId))
            .CollectAsync(context, CancellationToken.None);

        var unfiltered = await CreateCollector(
                Reader(),
                new NewsCollectorOptions { InterRequestDelay = TimeSpan.Zero, RecencyWindowDays = 0 })
            .CollectAsync(context, CancellationToken.None);

        Assert.Equal(
            unfiltered.Evidence.Select(e => (e.SourceUrl, e.Title, e.SourceName, e.PublishedAt)),
            windowed.Evidence.Select(e => (e.SourceUrl, e.Title, e.SourceName, e.PublishedAt)));
        Assert.Equal(
            unfiltered.Observations!.Select(o => (o.GoogleLandingUrl, o.Headline, o.Publisher)),
            windowed.Observations!.Select(o => (o.GoogleLandingUrl, o.Headline, o.Publisher)));
        Assert.Equal(unfiltered.Summary.ItemsCollected, windowed.Summary.ItemsCollected);
    }


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

    // -------------------------------------------------------------------------------------------------
    // Spec 200 §3: the three repaired production feed identities (UTMD / ITIC / ESQ), exercised through the
    // PUBLIC collection surface with the production company ids, tickers and phrases. `IsRelevant` stays
    // private and unchanged — these pin what the exact seed phrases make it decide.
    // -------------------------------------------------------------------------------------------------

    private static readonly Guid UtmdId = Guid.Parse("28243c9e-eb18-4a85-acec-8f93aeb8cdef");
    private static readonly Guid IticId = Guid.Parse("2ae6e6da-b714-416f-9d90-b6432f6eac2b");
    private static readonly Guid EsqId = Guid.Parse("971ea074-e524-4d6d-baf2-ead26449a0dc");

    private const string UtmdPhrase = "Utah Medical Products";
    private const string UtmdToken = "query=Utah Medical Products&ticker=UTMD";

    private const string IticPhrase = "Investors Title Company";
    private const string IticToken = "query=Investors Title Company";

    private const string EsqPhrase = "Esquire Financial";
    private const string EsqToken = "query=Esquire Financial";

    public static TheoryData<string, string> Spec200RejectedHeadlines => new()
    {
        // UTMD: the old phrase "Utah Medical" admitted a university plus the word "medical".
        { "UTMD", "University of Utah Medical School opens a new centre" },
        // ITIC: the old phrase "Investors Title" admitted "investors title <something>" as a theme.
        { "ITIC", "Investors title technology as their top theme" },
        // ESQ: the old ticker token "ESQ" admitted every headline containing the word "Esquire".
        { "ESQ", "Esquire names its people of the year" },
    };

    public static TheoryData<string, string> Spec200AcceptedHeadlines => new()
    {
        { "UTMD", "Utah Medical Products reports quarterly results" },
        { "ITIC", "Investors Title Company declares a dividend" },
        { "ESQ", "Esquire Financial expands litigation banking" },
    };

    [Theory]
    [MemberData(nameof(Spec200RejectedHeadlines))]
    public async Task CollectAsync_Spec200RepairedFeed_RejectsTheAdversarialHeadline(string ticker, string headline)
    {
        var (company, feed, phrase) = Spec200Fixture(ticker);
        var reader = new FakeNewsSearchReader
        {
            [phrase] = [Article("https://reject.example/" + ticker, headline + " - Reuters")],
        };

        var result = await CreateCollector(reader)
            .CollectAsync(new CollectionContext([company], [feed]), CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [MemberData(nameof(Spec200AcceptedHeadlines))]
    public async Task CollectAsync_Spec200RepairedFeed_AcceptsTheIssuerHeadline_ForTheIntendedCompany(
        string ticker, string headline)
    {
        var (company, feed, phrase) = Spec200Fixture(ticker);
        var reader = new FakeNewsSearchReader
        {
            [phrase] = [Article("https://accept.example/" + ticker, headline + " - Reuters")],
        };

        var result = await CreateCollector(reader)
            .CollectAsync(new CollectionContext([company], [feed]), CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        var item = Assert.Single(result.Evidence);
        Assert.Equal("https://accept.example/" + ticker, item.SourceUrl);
        // The evidence is bound to the INTENDED company: the hint comes from the feed binding (ticker) and
        // the observation sidecar carries the company id itself.
        Assert.Equal([ticker], item.CompanyHints);
        var observation = Assert.Single(result.Observations!);
        Assert.Equal(company.Id, observation.CompanyId);
        Assert.Equal(ticker, observation.Ticker);
    }

    [Theory]
    [MemberData(nameof(Spec200RejectedHeadlines))]
    public async Task CollectAsync_Spec200RepairedFeed_RejectedHeadline_ProducesNoObservationEither(
        string ticker, string headline)
    {
        var (company, feed, phrase) = Spec200Fixture(ticker);
        var reader = new FakeNewsSearchReader
        {
            [phrase] = [Article("https://reject.example/" + ticker, headline + " - Reuters")],
        };

        var result = await CreateCollector(reader)
            .CollectAsync(new CollectionContext([company], [feed]), CancellationToken.None);

        Assert.Empty(result.Observations ?? []);
    }

    private static (Company Company, CompanySourceFeed Feed, string Phrase) Spec200Fixture(string ticker) =>
        ticker switch
        {
            "UTMD" => (
                Company(UtmdId, "Utah Medical Products", "UTMD"),
                Feed(Guid.Parse("dddddddd-0000-0000-0000-000000000001"), UtmdId, "Utah Medical Products — News", UtmdToken),
                UtmdPhrase),
            "ITIC" => (
                Company(IticId, "Investors Title Company", "ITIC"),
                Feed(Guid.Parse("dddddddd-0000-0000-0000-000000000002"), IticId, "Investors Title Company — News", IticToken),
                IticPhrase),
            "ESQ" => (
                Company(EsqId, "Esquire Financial Holdings", "ESQ"),
                Feed(Guid.Parse("dddddddd-0000-0000-0000-000000000003"), EsqId, "Esquire Financial Holdings — News", EsqToken),
                EsqPhrase),
            _ => throw new ArgumentOutOfRangeException(nameof(ticker), ticker, "Not a spec-200 fixture ticker."),
        };

    // -------------------------------------------------------------------------------------------------
    // Spec 207 §2/§3: the two collision-bearing feed identities of the AI-robotics batch (OUST / CMCO),
    // exercised through the PUBLIC collection surface with the production company ids, tickers and phrases.
    // `IsRelevant` stays private and unchanged — these pin what the exact seed phrases make it decide.
    // The predicate reads the FEED TOKEN's phrase and ticker, never the seed company's own ticker (that one
    // only reaches the company hint and the observation sidecar), so OUST's token deliberately carries no
    // ticker= key and the reject cases below do not depend on the company ticker being absent.
    // -------------------------------------------------------------------------------------------------

    private static readonly Guid OustId = Guid.Parse("43ad746d-3a94-46b9-9cf6-d43bbc3cb8c4");
    private static readonly Guid CmcoId = Guid.Parse("d903f447-79e6-43be-b3e1-e58b051c9ea1");

    private const string OustPhrase = "Ouster Inc";
    private const string OustToken = "query=Ouster Inc";

    private const string CmcoPhrase = "Columbus McKinnon";
    private const string CmcoToken = "query=Columbus McKinnon&ticker=CMCO";

    public static TheoryData<string, string> Spec207RejectedHeadlines => new()
    {
        // OUST: "ouster" the common noun — the bare phrase "Ouster" or the ticker token "OUST" would admit
        // every removal-from-office headline. The phrase includes "Inc" and there is no ticker token.
        { "OUST", "Shareholders demand the CEO's ouster after proxy fight" },
        // CMCO: the bare word "Columbus" is the city; the two-word phrase is what keeps the feed on-topic.
        { "CMCO", "Columbus city council approves transit plan" },
    };

    public static TheoryData<string, string> Spec207AcceptedHeadlines => new()
    {
        { "OUST", "Ouster Inc. reports quarterly results" },
        { "CMCO", "Columbus McKinnon expands automation line" },
    };

    [Theory]
    [MemberData(nameof(Spec207RejectedHeadlines))]
    public async Task CollectAsync_Spec207CollisionFeed_RejectsTheAdversarialHeadline(string ticker, string headline)
    {
        var (company, feed, phrase) = Spec207Fixture(ticker);
        var reader = new FakeNewsSearchReader
        {
            [phrase] = [Article("https://reject.example/" + ticker, headline + " - Reuters")],
        };

        var result = await CreateCollector(reader)
            .CollectAsync(new CollectionContext([company], [feed]), CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        Assert.Empty(result.Evidence);
        Assert.Empty(result.Observations ?? []);
    }

    [Theory]
    [MemberData(nameof(Spec207AcceptedHeadlines))]
    public async Task CollectAsync_Spec207CollisionFeed_AcceptsTheIssuerHeadline_ForTheIntendedCompany(
        string ticker, string headline)
    {
        var (company, feed, phrase) = Spec207Fixture(ticker);
        var reader = new FakeNewsSearchReader
        {
            [phrase] = [Article("https://accept.example/" + ticker, headline + " - Reuters")],
        };

        var result = await CreateCollector(reader)
            .CollectAsync(new CollectionContext([company], [feed]), CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        var item = Assert.Single(result.Evidence);
        Assert.Equal("https://accept.example/" + ticker, item.SourceUrl);
        // The evidence is bound to the INTENDED company: the hint comes from the seed company's own ticker
        // (present even for OUST, whose feed token carries none) and the observation sidecar carries the
        // company id itself.
        Assert.Equal([ticker], item.CompanyHints);
        var observation = Assert.Single(result.Observations!);
        Assert.Equal(company.Id, observation.CompanyId);
        Assert.Equal(ticker, observation.Ticker);
    }

    /// <summary>
    /// The OUST recall side of the declared spec-207 §2 risk case, pinned so it is a known and counted miss
    /// rather than a surprise: a headline that names the company ONLY as "Ouster (OUST)" — without "Inc" —
    /// is dropped, because the phrase is <c>Ouster Inc</c> and there is no ticker token. If the three-run
    /// read in <c>docs/cohorts/ai-robotics-2026-09.md</c> shows this starving the feed, the remedy is a
    /// measured follow-up spec against the relevance predicate, never a quiet query edit (spec 207 §2).
    /// </summary>
    [Fact]
    public async Task CollectAsync_Spec207OustFeed_TickerOnlyHeadline_IsMissedByDesign()
    {
        var (company, feed, phrase) = Spec207Fixture("OUST");
        var reader = new FakeNewsSearchReader
        {
            [phrase] = [Article("https://miss.example/oust", "Ouster (OUST) shares rise on lidar order - Reuters")],
        };

        var result = await CreateCollector(reader)
            .CollectAsync(new CollectionContext([company], [feed]), CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        Assert.Empty(result.Evidence);
        Assert.Empty(result.Observations ?? []);
    }

    private static (Company Company, CompanySourceFeed Feed, string Phrase) Spec207Fixture(string ticker) =>
        ticker switch
        {
            "OUST" => (
                Company(OustId, "Ouster", "OUST"),
                Feed(Guid.Parse("eeeeeeee-0000-0000-0000-000000000001"), OustId, "Ouster — News", OustToken),
                OustPhrase),
            "CMCO" => (
                Company(CmcoId, "Columbus McKinnon", "CMCO"),
                Feed(Guid.Parse("eeeeeeee-0000-0000-0000-000000000002"), CmcoId, "Columbus McKinnon — News", CmcoToken),
                CmcoPhrase),
            _ => throw new ArgumentOutOfRangeException(nameof(ticker), ticker, "Not a spec-207 fixture ticker."),
        };

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

        /// <summary>Every query issued, in order — spec 198 needs the per-feed recency window, not just the phrase.</summary>
        public List<NewsSearchQuery> QueriesInOrder { get; } = [];

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
            QueriesInOrder.Add(query);
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
