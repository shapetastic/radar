using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Hiring;

namespace Radar.Infrastructure.Tests.Hiring;

public sealed class HiringBoardCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EriiId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string MrcyToken = "platform=greenhouse&board=mercury";
    private const string EriiToken = "platform=lever&board=energyrecovery";

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
        Guid id, Guid companyId, string name, string url, string feedType = "hiringats") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static HiringBoardCollector CreateCollector(
        IEnumerable<IJobBoardReader> readers, HiringCollectorOptions? options = null) =>
        new(
            readers,
            NullLogger<HiringBoardCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new HiringCollectorOptions());

    [Fact]
    public async Task CollectAsync_MapsBoardSnapshotToJobPostingEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId,
            "Mercury Systems — Open roles (Greenhouse ATS)", MrcyToken);

        var reader = new FakeJobBoardReader("greenhouse")
        {
            ["mercury"] = new JobBoardResult(
                TotalRoles: 4,
                Titles:
                [
                    "VP of Engineering",           // senior + engineering
                    "Senior Software Engineer",    // engineering
                    "VP, Strategic Partnerships",  // senior — must NEVER leak into Title/RawText
                    "Account Executive",           // neither
                ]),
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector([reader]).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.JobPosting, item.SourceType);
        Assert.Equal("Mercury Systems — Open roles (Greenhouse ATS)", item.SourceName);
        Assert.Equal("https://fake.greenhouse.example/mercury", item.SourceUrl);

        // The verbatim spec-103 phrase + counts in Title/RawText (the extractor contract).
        Assert.Equal(
            "Hiring activity (open roles) — 4 open roles (2 senior/leadership, 2 engineering/R&D) "
                + "via greenhouse board 'mercury'",
            item.Title);
        Assert.Equal(
            "greenhouse job board 'mercury': 4 open roles as of 2026-07-07T12:00:00.0000000+00:00; "
                + "2 senior/leadership, 2 engineering/R&D. Signal: hiring activity (open roles).",
            item.RawText);

        // NO raw job titles in Title/RawText — "VP, Strategic Partnerships" would trip the partnership rule.
        Assert.DoesNotContain("Partnerships", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Partnerships", item.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VP of Engineering", item.RawText, StringComparison.Ordinal);

        // Both instants are the injected TimeProvider's UTC now (a live snapshot has no publish date).
        Assert.Equal(FixedNow, item.PublishedAt);
        Assert.Equal(TimeSpan.Zero, item.PublishedAt!.Value.Offset);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + Medium quality; the counts are the accrued history slice B reads.
        Assert.Equal("Medium", item.Metadata["quality"]);
        Assert.Equal(MrcyToken, item.Metadata["hiringFeedUrl"]);
        Assert.Equal("greenhouse", item.Metadata["platform"]);
        Assert.Equal("mercury", item.Metadata["board"]);
        Assert.Equal("4", item.Metadata["totalRoles"]);
        Assert.Equal("2", item.Metadata["seniorRoles"]);
        Assert.Equal("2", item.Metadata["engRoles"]);
        Assert.Equal(
            "VP of Engineering | Senior Software Engineer | VP, Strategic Partnerships | Account Executive",
            item.Metadata["sampleTitles"]);
        Assert.Equal("2026-07-07T12:00:00.0000000+00:00", item.Metadata["retrievedAtUtc"]);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["MRCY"], item.CompanyHints);

        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
    }

    [Fact]
    public async Task CollectAsync_ZeroRoleBoard_IsAValidSuccessSnapshot()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — Hiring", MrcyToken);
        var reader = new FakeJobBoardReader("greenhouse")
        {
            ["mercury"] = new JobBoardResult(TotalRoles: 0, Titles: []),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector([reader]).CollectAsync(context, CancellationToken.None);

        // A board with no openings is still a valid timestamped snapshot (0 is a data point for slice B).
        var item = Assert.Single(result.Evidence);
        Assert.Contains("0 open roles", item.Title, StringComparison.Ordinal);
        Assert.Equal("0", item.Metadata["totalRoles"]);
        Assert.Equal(string.Empty, item.Metadata["sampleTitles"]);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxSampleTitles()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — Hiring", MrcyToken);
        var reader = new FakeJobBoardReader("greenhouse")
        {
            ["mercury"] = new JobBoardResult(3, ["Role A", "Role B", "Role C"]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new HiringCollectorOptions { MaxSampleTitles = 2 };

        var result = await CreateCollector([reader], options).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Role A | Role B", item.Metadata["sampleTitles"]);
        // The total still reflects the whole board, not the bounded sample.
        Assert.Equal("3", item.Metadata["totalRoles"]);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — Hiring",
            "not-a-valid-token");
        var reader = new FakeJobBoardReader("greenhouse");
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var logger = new CapturingLogger<HiringBoardCollector>();
        var collector = new HiringBoardCollector(
            [reader], logger, new FixedTimeProvider(FixedNow), new HiringCollectorOptions());

        var result = await collector.CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(0, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Mercury — Hiring", failure.SourceName);
        Assert.Equal("malformed hiringats feed token", failure.Reason);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CollectAsync_UnsupportedPlatform_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — Hiring",
            "platform=workday&board=mercury");
        var reader = new FakeJobBoardReader("greenhouse");
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector([reader]).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("unsupported hiring platform 'workday'", failure.Reason);
    }

    [Fact]
    public async Task CollectAsync_PlatformLookup_IsCaseInsensitive()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — Hiring",
            "platform=Greenhouse&board=mercury");
        var reader = new FakeJobBoardReader("greenhouse")
        {
            ["mercury"] = new JobBoardResult(1, ["Software Engineer"]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector([reader]).CollectAsync(context, CancellationToken.None);

        Assert.Single(result.Evidence);
        Assert.Equal(0, result.Summary.SourcesFailed);
    }

    [Fact]
    public async Task CollectAsync_ReaderFailure_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury — Hiring", MrcyToken);
        var reader = new FakeJobBoardReader("greenhouse");
        reader.SetFailure("mercury", JobBoardReadOutcome.HttpError, "HTTP 404");
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector([reader]).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(0, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("HTTP 404", failure.Reason);
        Assert.Equal(MrcyToken, failure.SourceUrl);
    }

    [Fact]
    public async Task CollectAsync_NoHiringFeeds_ReturnsEmptyAndNeverCallsReaders()
    {
        var rssFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Mercury RSS",
            "https://mrcy.test/rss", feedType: "rss");
        var reader = new FakeJobBoardReader("greenhouse");
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [rssFeed]);

        var result = await CreateCollector([reader]).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, result.Summary.SourcesChecked);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury — Hiring", MrcyToken);
        var reader = new FakeJobBoardReader("greenhouse")
        {
            ["mercury"] = new JobBoardResult(1, ["Software Engineer"]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", ticker: null)], [feed]);

        var result = await CreateCollector([reader]).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal(["Mercury Systems"], item.CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_MixedFeeds_CountsSummaryAndPreservesDeterministicOrder()
    {
        // MrcyId < EriiId, so FeedsOfType orders Mercury's feed first regardless of list order.
        var eriiFeed = Feed(
            Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), EriiId, "Energy Recovery — Hiring", EriiToken);
        var mrcyFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — Hiring", MrcyToken);

        var greenhouse = new FakeJobBoardReader("greenhouse")
        {
            ["mercury"] = new JobBoardResult(2, ["Software Engineer", "Director of Sales"]),
        };
        var lever = new FakeJobBoardReader("lever");
        // The Lever read fails, exercising the failed-count path alongside a successful feed.
        lever.SetFailure("energyrecovery", JobBoardReadOutcome.Timeout, "request timed out");

        var logger = new CapturingLogger<HiringBoardCollector>();
        var collector = new HiringBoardCollector(
            [greenhouse, lever], logger, new FixedTimeProvider(FixedNow), new HiringCollectorOptions());

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(EriiId, "Energy Recovery", "ERII")],
            [eriiFeed, mrcyFeed]);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Mercury — Hiring", item.SourceName);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Energy Recovery — Hiring", failure.SourceName);
        Assert.Equal("request timed out", failure.Reason);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Energy Recovery", warning.Message);
    }

    /// <summary>
    /// A fake per-platform board reader keyed by board token. <see cref="BoardUrl"/> returns a
    /// deterministic fake URL so the SourceUrl provenance assertion has a stable value.
    /// </summary>
    private sealed class FakeJobBoardReader(string platform) : IJobBoardReader
    {
        private readonly Dictionary<string, JobBoardReadResult> _byBoard = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public string Platform { get; } = platform;

        public JobBoardResult this[string boardToken]
        {
            set => _byBoard[boardToken] = JobBoardReadResult.Success(value);
        }

        public void SetFailure(string boardToken, JobBoardReadOutcome outcome, string detail) =>
            _byBoard[boardToken] = JobBoardReadResult.Failure(outcome, detail);

        public string BoardUrl(string boardToken) => $"https://fake.{Platform}.example/{boardToken}";

        public Task<JobBoardReadResult> ReadAsync(string boardToken, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(
                _byBoard.TryGetValue(boardToken, out var result)
                    ? result
                    : JobBoardReadResult.Success(new JobBoardResult(0, [])));
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
