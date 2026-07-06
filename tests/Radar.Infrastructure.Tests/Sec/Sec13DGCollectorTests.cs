using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class Sec13DGCollectorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

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

    private static CompanySourceFeed Feed(Guid id, Guid companyId, string name, string url, string feedType = "sec13dg") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static Sec13DGFiling Filing(
        string accession,
        Sec13DGCategory category,
        string form,
        string filingDate = "2026-06-02",
        DateTimeOffset? acceptance = null) =>
        new(
            Accession: accession,
            FilingDate: filingDate,
            AcceptanceDateTimeUtc: acceptance ?? new DateTimeOffset(2026, 6, 2, 20, 0, 0, TimeSpan.Zero),
            IndexUrl: $"https://www.sec.gov/Archives/edgar/data/1/{accession.Replace("-", string.Empty)}/{accession}-index.htm",
            Form: form,
            Category: category);

    private static Sec13DGCollector CreateCollector(
        FakeSec13DGReader reader, Sec13DGCollectorOptions? options = null) =>
        new(
            reader,
            NullLogger<Sec13DGCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new Sec13DGCollectorOptions { UserAgent = "Radar Research test@example.com" });

    [Fact]
    public async Task CollectAsync_Activist13D_MapsToActivistPhraseWithProvenanceAndMetadata()
    {
        const string url = "https://data.sec.gov/submissions/CIK0001049521.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId, "Mercury — 13D/13G", url);

        var acceptance = new DateTimeOffset(2026, 6, 2, 20, 0, 0, TimeSpan.Zero);
        var reader = new FakeSec13DGReader
        {
            [url] = [Filing("0001049521-26-000040", Sec13DGCategory.Activist13D, "SC 13D", acceptance: acceptance)],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.Filing, item.SourceType);
        Assert.Contains("activist beneficial-ownership stake (13d)", item.Title, StringComparison.Ordinal);
        Assert.Contains("activist beneficial-ownership stake (13d)", item.RawText, StringComparison.Ordinal);
        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/1/000104952126000040/0001049521-26-000040-index.htm",
            item.SourceUrl);
        Assert.Equal(acceptance, item.PublishedAt);
        Assert.Equal(FixedNow, item.CollectedAt);
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal("0001049521-26-000040", item.Metadata["accessionNumber"]);
        Assert.Equal("SC 13D", item.Metadata["form"]);
        Assert.Equal("Activist13D", item.Metadata["ownershipCategory"]);
        Assert.Equal(url, item.Metadata["secFeedUrl"]);
        Assert.Equal(["MRCY"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_Passive13G_MapsToPassivePhrase()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — 13D/13G", url);
        var reader = new FakeSec13DGReader
        {
            [url] = [Filing("acc-g", Sec13DGCategory.Passive13G, "SC 13G")],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Contains("passive beneficial-ownership stake (13g)", item.Title, StringComparison.Ordinal);
        Assert.Contains("passive beneficial-ownership stake (13g)", item.RawText, StringComparison.Ordinal);
        Assert.Equal("SC 13G", item.Metadata["form"]);
        Assert.Equal("Passive13G", item.Metadata["ownershipCategory"]);
    }

    [Fact]
    public async Task CollectAsync_Amendment_MapsToRoutineAmendmentPhrase()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — 13D/13G", url);
        var reader = new FakeSec13DGReader
        {
            [url] = [Filing("acc-da", Sec13DGCategory.Amendment, "SC 13D/A")],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Contains("beneficial-ownership amendment (routine)", item.Title, StringComparison.Ordinal);
        Assert.Contains("beneficial-ownership amendment (routine)", item.RawText, StringComparison.Ordinal);
        Assert.Equal("SC 13D/A", item.Metadata["form"]);
        Assert.Equal("Amendment", item.Metadata["ownershipCategory"]);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxFilingsPerCompany_KeepingNewestFirst()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — 13D/13G", url);
        var reader = new FakeSec13DGReader
        {
            [url] =
            [
                Filing("acc-newest", Sec13DGCategory.Passive13G, "SC 13G"),
                Filing("acc-middle", Sec13DGCategory.Passive13G, "SC 13G"),
                Filing("acc-oldest", Sec13DGCategory.Passive13G, "SC 13G"),
            ],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new Sec13DGCollectorOptions { UserAgent = "Radar Research test@example.com", MaxFilingsPerCompany = 2 };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        Assert.Equal(2, result.Evidence.Count);
        Assert.Contains(result.Evidence, e => e.Metadata["accessionNumber"] == "acc-newest");
        Assert.Contains(result.Evidence, e => e.Metadata["accessionNumber"] == "acc-middle");
        Assert.DoesNotContain(result.Evidence, e => e.Metadata["accessionNumber"] == "acc-oldest");
    }

    [Fact]
    public async Task CollectAsync_DedupesByAccessionWithinFeed()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — 13D/13G", url);
        var reader = new FakeSec13DGReader
        {
            [url] =
            [
                Filing("acc-dup", Sec13DGCategory.Activist13D, "SC 13D"),
                Filing("acc-dup", Sec13DGCategory.Activist13D, "SC 13D"),
            ],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_ForbiddenFeed_DegradesToSourceFailureWithoutThrowing()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — 13D/13G", url);
        var reader = new FakeSec13DGReader();
        reader.SetFailure(url, Sec13DGReadOutcome.Forbidden, "HTTP 403 (User-Agent)");

        var logger = new CapturingLogger<Sec13DGCollector>();
        var collector = new Sec13DGCollector(
            reader, logger, new FixedTimeProvider(FixedNow),
            new Sec13DGCollectorOptions { UserAgent = "Radar Research test@example.com" });

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(0, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("HTTP 403 (User-Agent)", failure.Reason);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CollectAsync_EmptyFeed_YieldsNoEvidenceAndCountsAsChecked()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Mercury — 13D/13G", url);
        var reader = new FakeSec13DGReader { [url] = [] };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
    }

    [Fact]
    public async Task CollectAsync_NoSec13DGFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var nonFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury RSS", "https://mrcy.test/rss",
            feedType: "rss");
        var reader = new FakeSec13DGReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [nonFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task CollectAsync_TwoFeeds_PreservesDeterministicOrderByCompanyId()
    {
        const string mrcyUrl = "https://data.sec.gov/submissions/CIK-mrcy.json";
        const string aehrUrl = "https://data.sec.gov/submissions/CIK-aehr.json";
        // MrcyId < AehrId, so FeedsOfType orders Mercury's feed first regardless of list order.
        var aehrFeed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), AehrId, "Aehr — 13D/13G", aehrUrl);
        var mrcyFeed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — 13D/13G", mrcyUrl);

        var reader = new FakeSec13DGReader
        {
            [mrcyUrl] = [Filing("acc-m", Sec13DGCategory.Activist13D, "SC 13D")],
            [aehrUrl] = [Filing("acc-a", Sec13DGCategory.Passive13G, "SC 13G")],
        };

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(AehrId, "Aehr Test", "AEHR")],
            [aehrFeed, mrcyFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var names = result.Evidence.Select(e => e.SourceName).ToList();

        Assert.Equal(["Mercury — 13D/13G", "Aehr — 13D/13G"], names);
    }

    private sealed class FakeSec13DGReader : ISec13DGReader
    {
        private readonly Dictionary<string, Sec13DGReadResult> _byUrl = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public IReadOnlyList<Sec13DGFiling> this[string url]
        {
            set => _byUrl[url] = Sec13DGReadResult.Success(value);
        }

        public void SetFailure(string url, Sec13DGReadOutcome outcome, string detail) =>
            _byUrl[url] = Sec13DGReadResult.Failure(outcome, detail);

        public Task<Sec13DGReadResult> ReadAsync(string submissionsUrl, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(
                _byUrl.TryGetValue(submissionsUrl, out var result) ? result : Sec13DGReadResult.Success([]));
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
