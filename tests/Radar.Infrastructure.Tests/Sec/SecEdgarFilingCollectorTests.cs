using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class SecEdgarFilingCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AehrId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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

    private static CompanySourceFeed Feed(Guid id, Guid companyId, string name, string url, string feedType = "sec") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static SecFilingItem Filing(
        string form,
        string filingDate,
        string accession,
        DateTimeOffset acceptanceUtc,
        string? items = null,
        string? primaryDocDescription = "Report",
        string? primaryDocument = "doc.htm") =>
        new(
            Form: form,
            FilingDate: filingDate,
            ReportDate: null,
            AcceptanceDateTimeUtc: acceptanceUtc,
            Accession: accession,
            PrimaryDocument: primaryDocument,
            PrimaryDocDescription: primaryDocDescription,
            Items: items,
            IndexUrl: $"https://www.sec.gov/Archives/edgar/data/1/{accession.Replace("-", string.Empty)}/{accession}-index.htm");

    private static SecEdgarFilingCollector CreateCollector(
        FakeSecFilingReader reader, SecCollectorOptions? options = null) =>
        new(
            reader,
            NullLogger<SecEdgarFilingCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new SecCollectorOptions { UserAgent = "Radar Research test@example.com" });

    [Fact]
    public async Task CollectAsync_MapsFilingsToFilingEvidenceWithProvenanceAndHints()
    {
        const string url = "https://data.sec.gov/submissions/CIK0001049521.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId, "Mercury — SEC", url);

        var acceptance = new DateTimeOffset(2026, 6, 2, 16, 30, 0, TimeSpan.Zero);
        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("8-K", "2026-06-02", "0001049521-26-000011", acceptance, items: "2.02,9.01"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.Filing, item.SourceType);
        Assert.Equal("Mercury — SEC", item.SourceName);
        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/1/000104952126000011/0001049521-26-000011-index.htm",
            item.SourceUrl);

        // The observed/published instant is the acceptance datetime (UTC), not CollectedAt.
        Assert.Equal(acceptance, item.PublishedAt);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + High quality.
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal("0001049521-26-000011", item.Metadata["accessionNumber"]);
        Assert.Equal("8-K", item.Metadata["form"]);
        Assert.Equal("2026-06-02", item.Metadata["filingDate"]);
        Assert.Equal(url, item.Metadata["secFeedUrl"]);
        Assert.Equal("doc.htm", item.Metadata["primaryDocument"]);

        // 8-K item codes surface in the title.
        Assert.Contains("2.02,9.01", item.Title);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["MRCY"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_KnownItemCodes_ExpandToOfficialTitlesWhileKeepingRawCodes()
    {
        const string url = "https://data.sec.gov/submissions/CIK0001049521.json";
        var feed = Feed(Guid.Parse("cccccccc-0000-0000-0000-000000000001"), MrcyId, "Mercury — SEC", url);

        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                // 2.02 resolves to a title; 9.01 (exhibits) is intentionally unmapped and must stay bare.
                Filing("8-K", "2026-06-02", "acc-1", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero), items: "2.02,9.01"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        // Raw codes preserved (provenance) AND the official item title appended (matchable text).
        Assert.Contains("2.02,9.01", item.RawText);
        Assert.Contains("Results of Operations and Financial Condition", item.RawText);
        // Unmapped 9.01 fabricates no title.
        Assert.DoesNotContain("9.01:", item.RawText);
    }

    [Fact]
    public async Task CollectAsync_MaterialDefinitiveAgreement_ExpandsToOfficialTitle()
    {
        const string url = "https://data.sec.gov/submissions/CIK0001049521.json";
        var feed = Feed(Guid.Parse("cccccccc-0000-0000-0000-000000000002"), MrcyId, "Mercury — SEC", url);

        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("8-K", "2026-06-02", "acc-1", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero), items: "1.01"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Contains("1.01", item.RawText);
        Assert.Contains("Entry into a Material Definitive Agreement", item.RawText);
    }

    [Fact]
    public async Task CollectAsync_UnmappedItemCodeAlone_LeavesBareCodeAndFabricatesNoTitle()
    {
        const string url = "https://data.sec.gov/submissions/CIK0001049521.json";
        var feed = Feed(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), MrcyId, "Mercury — SEC", url);

        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("8-K", "2026-06-02", "acc-1", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero), items: "9.01"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        // Bare code retained, no "Items:" clause synthesised (no fabricated title).
        Assert.Contains("9.01", item.RawText);
        Assert.DoesNotContain("Items:", item.RawText);
        Assert.DoesNotContain("Items:", item.Title);
    }

    [Fact]
    public async Task CollectAsync_NonEightKForm_HasNoItemsAndNoSynthesizedTitles()
    {
        const string url = "https://data.sec.gov/submissions/CIK0001049521.json";
        var feed = Feed(Guid.Parse("cccccccc-0000-0000-0000-000000000004"), MrcyId, "Mercury — SEC", url);

        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("10-Q", "2026-06-02", "acc-1", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero), items: null),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.DoesNotContain("Items:", item.RawText);
        Assert.DoesNotContain("8-K item codes", item.RawText);
    }

    [Fact]
    public async Task CollectAsync_FiltersToConfiguredForms()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — SEC", url);

        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("8-K", "2026-06-02", "acc-1", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                Filing("4", "2026-06-01", "acc-2", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)),
                Filing("SC 13G", "2026-05-30", "acc-3", new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("8-K", item.Metadata["form"]);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxFilingsPerCompany_KeepingMostRecent()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — SEC", url);

        // Newest-first: three 10-Qs. With a cap of 2 only the first two (most recent) are kept.
        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("10-Q", "2026-06-02", "acc-newest", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                Filing("10-Q", "2026-03-02", "acc-middle", new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero)),
                Filing("10-Q", "2025-12-02", "acc-oldest", new DateTimeOffset(2025, 12, 2, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new SecCollectorOptions
        {
            UserAgent = "Radar Research test@example.com",
            MaxFilingsPerCompany = 2,
        };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        Assert.Equal(2, result.Evidence.Count);
        Assert.Contains(result.Evidence, e => e.Metadata["accessionNumber"] == "acc-newest");
        Assert.Contains(result.Evidence, e => e.Metadata["accessionNumber"] == "acc-middle");
        Assert.DoesNotContain(result.Evidence, e => e.Metadata["accessionNumber"] == "acc-oldest");
    }

    [Fact]
    public async Task CollectAsync_SameFormDifferentDates_ProduceDifferentRawText()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — SEC", url);

        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("10-K", "2026-02-15", "acc-2026", new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero)),
                Filing("10-K", "2025-02-15", "acc-2025", new DateTimeOffset(2025, 2, 15, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var texts = result.Evidence.Select(e => e.RawText).ToList();

        Assert.Equal(2, texts.Count);
        Assert.NotEqual(texts[0], texts[1]);
    }

    [Fact]
    public async Task CollectAsync_DedupesByAccessionWithinFeed()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — SEC", url);

        var reader = new FakeSecFilingReader
        {
            [url] =
            [
                Filing("8-K", "2026-06-02", "acc-dup", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                Filing("8-K", "2026-06-02", "acc-dup", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — SEC", url);
        var reader = new FakeSecFilingReader
        {
            [url] = [Filing("8-K", "2026-06-02", "acc-1", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero))],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal(["Mercury Systems"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_NoSecFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var nonSec = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury RSS", "https://mrcy.test/rss",
            feedType: "rss");
        var reader = new FakeSecFilingReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [nonSec]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task CollectAsync_DelistedOrEmptyFeed_ProducesNoEvidenceWithoutThrowing()
    {
        const string url = "https://data.sec.gov/submissions/CIK-empty.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Delisted — SEC", url);
        var reader = new FakeSecFilingReader { [url] = [] };
        var context = new CollectionContext([Company(MrcyId, "Delisted Co", "DEAD")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
    }

    [Fact]
    public async Task CollectAsync_ForbiddenFeed_IsLoggedAsFailureWhileOtherFeedSucceeds()
    {
        const string mrcyUrl = "https://data.sec.gov/submissions/CIK-mrcy.json";
        const string aehrUrl = "https://data.sec.gov/submissions/CIK-aehr.json";
        var mrcyFeed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury — SEC", mrcyUrl);
        var aehrFeed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000009"), AehrId, "Aehr — SEC", aehrUrl);

        var reader = new FakeSecFilingReader
        {
            [aehrUrl] = [Filing("8-K", "2026-06-02", "acc-1", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero))],
        };
        reader.SetFailure(mrcyUrl, SecFilingReadOutcome.Forbidden, "HTTP 403 (User-Agent)");

        var logger = new CapturingLogger<SecEdgarFilingCollector>();
        var collector = new SecEdgarFilingCollector(
            reader,
            logger,
            new FixedTimeProvider(FixedNow),
            new SecCollectorOptions { UserAgent = "Radar Research test@example.com" });

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(AehrId, "Aehr Test", "AEHR")],
            [mrcyFeed, aehrFeed]);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Aehr — SEC", item.SourceName);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Mercury — SEC", warning.Message);
        Assert.Contains(mrcyUrl, warning.Message);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Mercury — SEC", failure.SourceName);
        Assert.Equal(mrcyUrl, failure.SourceUrl);
        Assert.Equal("HTTP 403 (User-Agent)", failure.Reason);
    }

    [Fact]
    public async Task CollectAsync_TwoFeeds_PreservesDeterministicOrder()
    {
        const string mrcyUrl = "https://data.sec.gov/submissions/CIK-mrcy.json";
        const string aehrUrl = "https://data.sec.gov/submissions/CIK-aehr.json";
        // MrcyId < AehrId, so FeedsOfType orders Mercury's feed first regardless of list order.
        var aehrFeed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), AehrId, "Aehr — SEC", aehrUrl);
        var mrcyFeed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — SEC", mrcyUrl);

        var reader = new FakeSecFilingReader
        {
            [mrcyUrl] = [Filing("8-K", "2026-06-02", "acc-m", new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero))],
            [aehrUrl] = [Filing("8-K", "2026-06-01", "acc-a", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))],
        };

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(AehrId, "Aehr Test", "AEHR")],
            [aehrFeed, mrcyFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var names = result.Evidence.Select(e => e.SourceName).ToList();

        Assert.Equal(["Mercury — SEC", "Aehr — SEC"], names);
    }

    private sealed class FakeSecFilingReader : ISecFilingReader
    {
        private readonly Dictionary<string, SecFilingReadResult> _byUrl = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public IReadOnlyList<SecFilingItem> this[string url]
        {
            set => _byUrl[url] = SecFilingReadResult.Success(value);
        }

        public void SetFailure(string url, SecFilingReadOutcome outcome, string detail) =>
            _byUrl[url] = SecFilingReadResult.Failure(outcome, detail);

        public Task<SecFilingReadResult> ReadAsync(string submissionsUrl, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(
                _byUrl.TryGetValue(submissionsUrl, out var result) ? result : SecFilingReadResult.Success([]));
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
