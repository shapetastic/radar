using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.UsaSpending;

namespace Radar.Infrastructure.Tests.UsaSpending;

public sealed class UsaSpendingContractCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgysId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string MrcyRecipientId = "af09eaba-71de-97b6-660d-1adac9349c4d-C";
    private const string MrcySearchText = "Mercury Systems";

    private static string MrcyToken =>
        $"recipientId={MrcyRecipientId}&recipientSearchText={MrcySearchText}";

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
        Guid id, Guid companyId, string name, string url, string feedType = "usaspending") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static UsaSpendingAwardItem Award(
        string awardId,
        string generatedInternalId,
        string recipientId,
        decimal amount = 100000m,
        string startDate = "2026-03-24",
        string agency = "Department of Defense",
        string recipientName = "MERCURY SYSTEMS INC",
        string? description = "Processor cards") =>
        new(
            AwardId: awardId,
            RecipientName: recipientName,
            AwardAmount: amount,
            AwardingAgency: agency,
            StartDate: startDate,
            EndDate: null,
            Description: description,
            RecipientId: recipientId,
            GeneratedInternalId: generatedInternalId,
            AwardUrl: $"https://www.usaspending.gov/award/{generatedInternalId}");

    private static UsaSpendingContractCollector CreateCollector(
        FakeUsaSpendingAwardReader reader, UsaSpendingCollectorOptions? options = null) =>
        new(
            reader,
            NullLogger<UsaSpendingContractCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new UsaSpendingCollectorOptions());

    [Fact]
    public async Task CollectAsync_MapsAwardsToGovernmentContractEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId, "Mercury — USASpending", MrcyToken);

        var reader = new FakeUsaSpendingAwardReader
        {
            [MrcySearchText] =
            [
                Award("N6893626P5106", "CONT_AWD_5106", MrcyRecipientId, amount: 159160m, startDate: "2026-03-24"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.GovernmentContract, item.SourceType);
        Assert.Equal("Mercury — USASpending", item.SourceName);
        Assert.Equal("https://www.usaspending.gov/award/CONT_AWD_5106", item.SourceUrl);

        // PublishedAt is the award Start Date parsed as UTC midnight; CollectedAt is the TimeProvider now.
        Assert.Equal(new DateTimeOffset(2026, 3, 24, 0, 0, 0, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal(TimeSpan.Zero, item.PublishedAt!.Value.Offset);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + High quality.
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal("N6893626P5106", item.Metadata["awardId"]);
        Assert.Equal("CONT_AWD_5106", item.Metadata["generatedInternalId"]);
        Assert.Equal(MrcyRecipientId, item.Metadata["recipientId"]);
        Assert.Equal("Department of Defense", item.Metadata["awardingAgency"]);
        Assert.Equal("2026-03-24", item.Metadata["startDate"]);
        Assert.Equal(MrcyToken, item.Metadata["usaSpendingFeedUrl"]);

        // Award id + generated internal id appear in the hashed RawText so distinct awards never collide.
        Assert.Contains("N6893626P5106", item.RawText);
        Assert.Contains("CONT_AWD_5106", item.RawText);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["MRCY"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_DropsAwardsWhoseRecipientIdDiffersFromFeed()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — USASpending", MrcyToken);

        // The fuzzy search returns the real recipient plus a subsidiary/unrelated entity with a different id.
        var reader = new FakeUsaSpendingAwardReader
        {
            [MrcySearchText] =
            [
                Award("A-KEEP", "CONT_AWD_KEEP", MrcyRecipientId),
                Award("A-DROP", "CONT_AWD_DROP", "physical-optics-subsidiary-id-C", recipientName: "PHYSICAL OPTICS CORPORATION"),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("A-KEEP", item.Metadata["awardId"]);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxAwardsPerCompany_KeepingHighestFirst()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — USASpending", MrcyToken);

        // The reader returns awards already sorted by amount desc (as the API does).
        var reader = new FakeUsaSpendingAwardReader
        {
            [MrcySearchText] =
            [
                Award("A-1", "CONT_AWD_1", MrcyRecipientId, amount: 300000m),
                Award("A-2", "CONT_AWD_2", MrcyRecipientId, amount: 200000m),
                Award("A-3", "CONT_AWD_3", MrcyRecipientId, amount: 100000m),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new UsaSpendingCollectorOptions { MaxAwardsPerCompany = 2 };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        Assert.Equal(2, result.Evidence.Count);
        Assert.Contains(result.Evidence, e => e.Metadata["awardId"] == "A-1");
        Assert.Contains(result.Evidence, e => e.Metadata["awardId"] == "A-2");
        Assert.DoesNotContain(result.Evidence, e => e.Metadata["awardId"] == "A-3");
    }

    [Fact]
    public async Task CollectAsync_DedupesByGeneratedInternalIdWithinFeed()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — USASpending", MrcyToken);

        var reader = new FakeUsaSpendingAwardReader
        {
            [MrcySearchText] =
            [
                Award("A-DUP", "CONT_AWD_DUP", MrcyRecipientId),
                Award("A-DUP", "CONT_AWD_DUP", MrcyRecipientId),
            ],
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task CollectAsync_MisconfiguredLookbackDays_ClampsToApiFloorWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury — USASpending", MrcyToken);
        var reader = new FakeUsaSpendingAwardReader
        {
            [MrcySearchText] = [Award("A-1", "CONT_AWD_1", MrcyRecipientId)],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        // A config-driven LookbackDays this large would overflow DateTimeOffset.AddDays if unclamped.
        var options = new UsaSpendingCollectorOptions { LookbackDays = int.MaxValue };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
        Assert.NotNull(reader.LastQuery);
        Assert.Equal("2007-10-01", reader.LastQuery!.StartDate);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — USASpending",
            "not-a-valid-token");
        var reader = new FakeUsaSpendingAwardReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(0, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Mercury — USASpending", failure.SourceName);
    }

    [Fact]
    public async Task CollectAsync_FiltersIgnoredRead_DegradesToSourceFailureWithNoEvidence()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — USASpending", MrcyToken);
        var reader = new FakeUsaSpendingAwardReader();
        reader.SetFailure(MrcySearchText, UsaSpendingReadOutcome.FiltersIgnored, "filters were not used (firehose guard)");

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(0, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("filters were not used (firehose guard)", failure.Reason);
    }

    [Fact]
    public async Task CollectAsync_RecipientWithNoAwards_ProducesNoEvidenceWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury — USASpending", MrcyToken);
        var reader = new FakeUsaSpendingAwardReader { [MrcySearchText] = [] };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
    }

    [Fact]
    public async Task CollectAsync_NoUsaSpendingFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var nonUsa = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Mercury RSS", "https://mrcy.test/rss",
            feedType: "rss");
        var reader = new FakeUsaSpendingAwardReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [nonUsa]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury — USASpending", MrcyToken);
        var reader = new FakeUsaSpendingAwardReader
        {
            [MrcySearchText] = [Award("A-1", "CONT_AWD_1", MrcyRecipientId)],
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal(["Mercury Systems"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_MixedFeeds_CountsSummaryAndPreservesDeterministicOrder()
    {
        const string agysSearch = "Agilysys";
        const string agysRecipientId = "5a343048-e1bb-6455-195f-d2213057e618-C";
        var agysToken = $"recipientId={agysRecipientId}&recipientSearchText={agysSearch}";

        // MrcyId < AgysId, so FeedsOfType orders Mercury's feed first regardless of list order.
        var agysFeed = Feed(Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), AgysId, "Agilysys — USASpending", agysToken);
        var mrcyFeed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — USASpending", MrcyToken);

        var reader = new FakeUsaSpendingAwardReader
        {
            [MrcySearchText] = [Award("A-M", "CONT_AWD_M", MrcyRecipientId)],
        };
        // Agilysys read fails, exercising the failed-count path alongside a successful feed.
        reader.SetFailure(agysSearch, UsaSpendingReadOutcome.HttpError, "HTTP 400");

        var logger = new CapturingLogger<UsaSpendingContractCollector>();
        var collector = new UsaSpendingContractCollector(
            reader, logger, new FixedTimeProvider(FixedNow), new UsaSpendingCollectorOptions());

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(AgysId, "Agilysys", "AGYS")],
            [agysFeed, mrcyFeed]);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Mercury — USASpending", item.SourceName);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Agilysys — USASpending", failure.SourceName);
        Assert.Equal(agysToken, failure.SourceUrl);
        Assert.Equal("HTTP 400", failure.Reason);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Agilysys", warning.Message);
    }

    private sealed class FakeUsaSpendingAwardReader : IUsaSpendingAwardReader
    {
        private readonly Dictionary<string, UsaSpendingReadResult> _bySearchText = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public UsaSpendingAwardQuery? LastQuery { get; private set; }

        public IReadOnlyList<UsaSpendingAwardItem> this[string searchText]
        {
            set => _bySearchText[searchText] = UsaSpendingReadResult.Success(value);
        }

        public void SetFailure(string searchText, UsaSpendingReadOutcome outcome, string detail) =>
            _bySearchText[searchText] = UsaSpendingReadResult.Failure(outcome, detail);

        public Task<UsaSpendingReadResult> ReadAsync(UsaSpendingAwardQuery query, CancellationToken ct)
        {
            ReadCount++;
            LastQuery = query;
            return Task.FromResult(
                _bySearchText.TryGetValue(query.SearchText, out var result)
                    ? result
                    : UsaSpendingReadResult.Success([]));
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
