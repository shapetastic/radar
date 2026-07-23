using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Patents;

namespace Radar.Infrastructure.Tests.Patents;

public sealed class PatentActivityCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EriiId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string MrcyToken = "assignee=Mercury Systems, Inc.";
    private const string EriiToken = "assignee=Energy Recovery, Inc.";

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
        Guid id, Guid companyId, string name, string url, string feedType = "patents") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static PatentActivityCollector CreateCollector(
        IPatentSearchReader reader, PatentCollectorOptions? options = null, ILogger<PatentActivityCollector>? logger = null) =>
        new(
            reader,
            logger ?? NullLogger<PatentActivityCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new PatentCollectorOptions());

    [Fact]
    public async Task CollectAsync_MapsSearchToPatentEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId,
            "Mercury Systems — Recent granted patents (PatentsView)", MrcyToken);

        var reader = new FakePatentSearchReader
        {
            ["Mercury Systems, Inc."] = new PatentSearchResult(
                GrantCount: 3,
                ApiReportedTotal: 41,
                Grants:
                [
                    // A patent title that would trip 'launches'/'new platform' — must stay in metadata ONLY.
                    new PatentGrant("11111111", "System for autonomous launch integration", new DateOnly(2026, 6, 1)),
                    new PatentGrant("22222222", "Radiation-hardened memory device", new DateOnly(2026, 5, 2)),
                    new PatentGrant("33333333", "Secure processing module", new DateOnly(2026, 4, 3)),
                ]),
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.Patent, item.SourceType);
        Assert.Equal("Mercury Systems — Recent granted patents (PatentsView)", item.SourceName);
        Assert.Equal("https://fake.patentsview.example/Mercury Systems, Inc.", item.SourceUrl);

        // The verbatim spec-127 phrase + count in Title/RawText (the extractor contract).
        Assert.Equal(
            "Patent activity (recent grants) — 3 patents granted to 'Mercury Systems, Inc.' "
                + "in the last 180 days",
            item.Title);
        Assert.Equal(
            "Assignee 'Mercury Systems, Inc.': 3 patents granted since 2026-01-24, as of "
                + "2026-07-23T12:00:00.0000000+00:00. Signal: patent activity (recent grants).",
            item.RawText);

        // NO raw patent titles in Title/RawText — "System for autonomous launch integration" would trip
        // the launches/new-platform rules.
        Assert.DoesNotContain("launch", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("launch", item.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory device", item.RawText, StringComparison.OrdinalIgnoreCase);

        // Both instants are the injected TimeProvider's UTC now (a window snapshot has no publish date).
        Assert.Equal(FixedNow, item.PublishedAt);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + High quality; the count is the accrued history slice B reads.
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal(MrcyToken, item.Metadata["patentsFeedUrl"]);
        Assert.Equal("Mercury Systems, Inc.", item.Metadata["assignee"]);
        Assert.Equal("3", item.Metadata["grantCount"]);
        Assert.Equal("180", item.Metadata["lookbackDays"]);
        Assert.Equal("2026-01-24", item.Metadata["grantFloor"]);
        Assert.Equal("41", item.Metadata["apiReportedTotal"]);
        Assert.Equal(
            "11111111: System for autonomous launch integration | 22222222: Radiation-hardened memory device "
                + "| 33333333: Secure processing module",
            item.Metadata["sampleTitles"]);
        Assert.Equal("2026-07-23T12:00:00.0000000+00:00", item.Metadata["retrievedAtUtc"]);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["MRCY"], item.CompanyHints);

        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
    }

    [Fact]
    public async Task CollectAsync_GrantFloorIsNowMinusLookbackDays()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — Patents", MrcyToken);
        var reader = new FakePatentSearchReader
        {
            ["Mercury Systems, Inc."] = new PatentSearchResult(1, 1, [new PatentGrant("1", "X", new DateOnly(2026, 6, 1))]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new PatentCollectorOptions { LookbackDays = 90 };

        await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        // now (2026-07-23) − 90 days = 2026-04-24.
        Assert.Equal(new DateOnly(2026, 4, 24), reader.LastGrantFloor);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxSampleTitles()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — Patents", MrcyToken);
        var reader = new FakePatentSearchReader
        {
            ["Mercury Systems, Inc."] = new PatentSearchResult(
                3, 3,
                [
                    new PatentGrant("1", "Alpha", new DateOnly(2026, 6, 1)),
                    new PatentGrant("2", "Beta", new DateOnly(2026, 5, 1)),
                    new PatentGrant("3", "Gamma", new DateOnly(2026, 4, 1)),
                ]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new PatentCollectorOptions { MaxSampleTitles = 2 };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("1: Alpha | 2: Beta", item.Metadata["sampleTitles"]);
        // The count still reflects the whole page, not the bounded sample.
        Assert.Equal("3", item.Metadata["grantCount"]);
    }

    [Fact]
    public async Task CollectAsync_ZeroGrants_IsAValidSuccessSnapshot()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — Patents", MrcyToken);
        var reader = new FakePatentSearchReader
        {
            ["Mercury Systems, Inc."] = new PatentSearchResult(0, 0, []),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Contains("0 patents granted", item.Title, StringComparison.Ordinal);
        Assert.Equal("0", item.Metadata["grantCount"]);
        Assert.Equal(string.Empty, item.Metadata["sampleTitles"]);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — Patents", "not-a-valid-token");
        var reader = new FakePatentSearchReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var logger = new CapturingLogger<PatentActivityCollector>();
        var result = await CreateCollector(reader, logger: logger).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("malformed patents feed token", failure.Reason);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CollectAsync_MissingApiKey_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), MrcyId, "Mercury — Patents", MrcyToken);
        var reader = new FakePatentSearchReader();
        reader.SetFailure("Mercury Systems, Inc.", PatentSearchOutcome.MissingApiKey, "API-key env var not set");
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("API-key env var not set", failure.Reason);
        Assert.Equal(MrcyToken, failure.SourceUrl);
    }

    [Fact]
    public async Task CollectAsync_ReaderFailure_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury — Patents", MrcyToken);
        var reader = new FakePatentSearchReader();
        reader.SetFailure("Mercury Systems, Inc.", PatentSearchOutcome.HttpError, "HTTP 403");
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal("HTTP 403", Assert.Single(result.Summary.Failures).Reason);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Mercury — Patents", MrcyToken);
        var reader = new FakePatentSearchReader
        {
            ["Mercury Systems, Inc."] = new PatentSearchResult(1, 1, [new PatentGrant("1", "X", new DateOnly(2026, 6, 1))]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Equal(["Mercury Systems"], Assert.Single(result.Evidence).CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_NoPatentsFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var rssFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury RSS",
            "https://mrcy.test/rss", feedType: "rss");
        var reader = new FakePatentSearchReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [rssFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, result.Summary.SourcesChecked);
    }

    [Fact]
    public async Task CollectAsync_MixedFeeds_CountsSummaryAndPreservesDeterministicOrder()
    {
        // MrcyId < EriiId, so FeedsOfType orders Mercury's feed first regardless of list order.
        var eriiFeed = Feed(
            Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), EriiId, "Energy Recovery — Patents", EriiToken);
        var mrcyFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — Patents", MrcyToken);

        var reader = new FakePatentSearchReader
        {
            ["Mercury Systems, Inc."] = new PatentSearchResult(2, 2,
                [new PatentGrant("1", "A", new DateOnly(2026, 6, 1)), new PatentGrant("2", "B", new DateOnly(2026, 5, 1))]),
        };
        // The Energy Recovery read fails, exercising the failed-count path alongside a successful feed.
        reader.SetFailure("Energy Recovery, Inc.", PatentSearchOutcome.Timeout, "request timed out");

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(EriiId, "Energy Recovery", "ERII")],
            [eriiFeed, mrcyFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Mercury — Patents", item.SourceName);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Energy Recovery — Patents", failure.SourceName);
        Assert.Equal("request timed out", failure.Reason);
    }

    /// <summary>
    /// A fake reader keyed by assignee name. <see cref="QueryUrl"/> returns a deterministic fake URL so the
    /// SourceUrl provenance assertion has a stable value; <see cref="LastGrantFloor"/> captures the computed
    /// window floor.
    /// </summary>
    private sealed class FakePatentSearchReader : IPatentSearchReader
    {
        private readonly Dictionary<string, PatentSearchReadResult> _byAssignee = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public DateOnly LastGrantFloor { get; private set; }

        public PatentSearchResult this[string assignee]
        {
            set => _byAssignee[assignee] = PatentSearchReadResult.Success(value);
        }

        public void SetFailure(string assignee, PatentSearchOutcome outcome, string detail) =>
            _byAssignee[assignee] = PatentSearchReadResult.Failure(outcome, detail);

        public string QueryUrl(string assigneeName, DateOnly grantFloor) =>
            $"https://fake.patentsview.example/{assigneeName}";

        public Task<PatentSearchReadResult> ReadAsync(
            string assigneeName, DateOnly grantFloor, CancellationToken ct)
        {
            ReadCount++;
            LastGrantFloor = grantFloor;
            return Task.FromResult(
                _byAssignee.TryGetValue(assigneeName, out var result)
                    ? result
                    : PatentSearchReadResult.Success(new PatentSearchResult(0, 0, [])));
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
