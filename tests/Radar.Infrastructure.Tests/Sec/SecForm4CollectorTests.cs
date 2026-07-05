using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class SecForm4CollectorTests
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

    private static CompanySourceFeed Feed(Guid id, Guid companyId, string name, string url, string feedType = "secform4") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static SecForm4Filing Filing(
        string accession,
        SignalDirection direction,
        decimal netValue,
        decimal shares = 1000m,
        string owner = "JANE DOE",
        string filingDate = "2026-06-02",
        DateTimeOffset? acceptance = null,
        bool hasCluster = false,
        bool is10b5Plan = false,
        string? ticker = "MRCY") =>
        new(
            Accession: accession,
            FilingDate: filingDate,
            AcceptanceDateTimeUtc: acceptance ?? new DateTimeOffset(2026, 6, 2, 20, 0, 0, TimeSpan.Zero),
            IndexUrl: $"https://www.sec.gov/Archives/edgar/data/1/{accession.Replace("-", string.Empty)}/{accession}-index.htm",
            IssuerTicker: ticker,
            PrimaryOwnerName: owner,
            DistinctOwnerCount: hasCluster ? 2 : 1,
            Direction: direction,
            NetValue: netValue,
            Shares: shares,
            HasCluster: hasCluster,
            Is10b5Plan: is10b5Plan);

    private static SecForm4Collector CreateCollector(
        FakeSecForm4Reader reader, SecForm4CollectorOptions? options = null) =>
        new(
            reader,
            NullLogger<SecForm4Collector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new SecForm4CollectorOptions { UserAgent = "Radar Research test@example.com" });

    [Fact]
    public async Task CollectAsync_Purchase_MapsToPositivePhraseWithProvenanceAndNetValueMetadata()
    {
        const string url = "https://data.sec.gov/submissions/CIK0001049521.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId, "Mercury — Form 4", url);

        var acceptance = new DateTimeOffset(2026, 6, 2, 20, 0, 0, TimeSpan.Zero);
        var reader = new FakeSecForm4Reader
        {
            [url] = [Filing("0001049521-26-000030", SignalDirection.Positive, 50_000m, acceptance: acceptance)],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.Filing, item.SourceType);
        Assert.Contains("insider open-market purchase", item.Title, StringComparison.Ordinal);
        Assert.Contains("insider open-market purchase", item.RawText, StringComparison.Ordinal);
        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/1/000104952126000030/0001049521-26-000030-index.htm",
            item.SourceUrl);
        Assert.Equal(acceptance, item.PublishedAt);
        Assert.Equal(FixedNow, item.CollectedAt);
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal("0001049521-26-000030", item.Metadata["accessionNumber"]);
        Assert.Equal("4", item.Metadata["form"]);
        Assert.Equal("50000", item.Metadata["insiderNetValue"]);
        Assert.Equal("Positive", item.Metadata["insiderDirection"]);
        Assert.Equal(["MRCY"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_Sale_MapsToNegativePhrase()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — Form 4", url);
        var reader = new FakeSecForm4Reader
        {
            [url] = [Filing("acc-s", SignalDirection.Negative, 915_750m)],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Contains("insider open-market sale", item.Title, StringComparison.Ordinal);
        Assert.Equal("915750", item.Metadata["insiderNetValue"]);
    }

    [Fact]
    public async Task CollectAsync_NeutralRoutine_OmitsInsiderNetValueMetadata()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — Form 4", url);
        var reader = new FakeSecForm4Reader
        {
            [url] = [Filing("acc-a", SignalDirection.Neutral, 0m)],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Contains("insider stock transaction (routine)", item.Title, StringComparison.Ordinal);
        Assert.False(item.Metadata.ContainsKey("insiderNetValue"));
    }

    [Fact]
    public async Task CollectAsync_ClusterFiling_EmitsInsiderClusterMetadata()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000b"), MrcyId, "Mercury — Form 4", url);
        var reader = new FakeSecForm4Reader
        {
            [url] = [Filing("acc-cluster", SignalDirection.Positive, 1_000_000m, hasCluster: true)],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal("true", item.Metadata["insiderCluster"]);
    }

    [Fact]
    public async Task CollectAsync_NonClusterFiling_OmitsInsiderClusterMetadata()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000c"), MrcyId, "Mercury — Form 4", url);
        var reader = new FakeSecForm4Reader
        {
            [url] = [Filing("acc-single", SignalDirection.Positive, 1_000_000m, hasCluster: false)],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.False(item.Metadata.ContainsKey("insiderCluster"));
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxFilingsPerCompany_KeepingNewestFirst()
    {
        const string url = "https://data.sec.gov/submissions/CIK.json";
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — Form 4", url);
        var reader = new FakeSecForm4Reader
        {
            [url] =
            [
                Filing("acc-newest", SignalDirection.Positive, 10_000m),
                Filing("acc-middle", SignalDirection.Positive, 10_000m),
                Filing("acc-oldest", SignalDirection.Positive, 10_000m),
            ],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new SecForm4CollectorOptions { UserAgent = "Radar Research test@example.com", MaxFilingsPerCompany = 2 };

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
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — Form 4", url);
        var reader = new FakeSecForm4Reader
        {
            [url] =
            [
                Filing("acc-dup", SignalDirection.Positive, 10_000m),
                Filing("acc-dup", SignalDirection.Positive, 10_000m),
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
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — Form 4", url);
        var reader = new FakeSecForm4Reader();
        reader.SetFailure(url, SecForm4ReadOutcome.Forbidden, "HTTP 403 (User-Agent)");

        var logger = new CapturingLogger<SecForm4Collector>();
        var collector = new SecForm4Collector(
            reader, logger, new FixedTimeProvider(FixedNow),
            new SecForm4CollectorOptions { UserAgent = "Radar Research test@example.com" });

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
    public async Task CollectAsync_NoSecForm4Feeds_ReturnsEmptyAndNeverCallsReader()
    {
        var nonForm4 = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury RSS", "https://mrcy.test/rss",
            feedType: "rss");
        var reader = new FakeSecForm4Reader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [nonForm4]);

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
        var aehrFeed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), AehrId, "Aehr — Form 4", aehrUrl);
        var mrcyFeed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — Form 4", mrcyUrl);

        var reader = new FakeSecForm4Reader
        {
            [mrcyUrl] = [Filing("acc-m", SignalDirection.Positive, 10_000m)],
            [aehrUrl] = [Filing("acc-a", SignalDirection.Positive, 10_000m)],
        };

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(AehrId, "Aehr Test", "AEHR")],
            [aehrFeed, mrcyFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var names = result.Evidence.Select(e => e.SourceName).ToList();

        Assert.Equal(["Mercury — Form 4", "Aehr — Form 4"], names);
    }

    private sealed class FakeSecForm4Reader : ISecForm4Reader
    {
        private readonly Dictionary<string, SecForm4ReadResult> _byUrl = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public IReadOnlyList<SecForm4Filing> this[string url]
        {
            set => _byUrl[url] = SecForm4ReadResult.Success(value);
        }

        public void SetFailure(string url, SecForm4ReadOutcome outcome, string detail) =>
            _byUrl[url] = SecForm4ReadResult.Failure(outcome, detail);

        public Task<SecForm4ReadResult> ReadAsync(string submissionsUrl, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(
                _byUrl.TryGetValue(submissionsUrl, out var result) ? result : SecForm4ReadResult.Success([]));
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
