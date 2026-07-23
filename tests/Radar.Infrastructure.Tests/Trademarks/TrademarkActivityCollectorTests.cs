using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Trademarks;

namespace Radar.Infrastructure.Tests.Trademarks;

public sealed class TrademarkActivityCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid WdfcId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HrlId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string WdfcToken = "owner=WD-40 Company";
    private const string HrlToken = "owner=Hormel Foods Corporation";

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
        Guid id, Guid companyId, string name, string url, string feedType = "trademarks") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static TrademarkActivityCollector CreateCollector(
        ITrademarkSearchReader reader,
        TrademarkCollectorOptions? options = null,
        ILogger<TrademarkActivityCollector>? logger = null) =>
        new(
            reader,
            logger ?? NullLogger<TrademarkActivityCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new TrademarkCollectorOptions());

    [Fact]
    public async Task CollectAsync_MapsSearchToTrademarkEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), WdfcId,
            "WD-40 — Recent trademark filings (USPTO)", WdfcToken);

        var reader = new FakeTrademarkSearchReader
        {
            ["WD-40 Company"] = new TrademarkSearchResult(
                FilingCount: 3,
                ApiReportedTotal: 41,
                Filings:
                [
                    // A wordmark that would trip 'rolls out'/'new platform' — must stay in metadata ONLY.
                    new TrademarkFiling("97000001", "ROLLS OUT PRO", new DateOnly(2026, 6, 1)),
                    new TrademarkFiling("97000002", "BLUE WORKS", new DateOnly(2026, 5, 2)),
                    new TrademarkFiling("97000003", "SPECIALIST", new DateOnly(2026, 4, 3)),
                ]),
        };

        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.Trademark, item.SourceType);
        Assert.Equal("WD-40 — Recent trademark filings (USPTO)", item.SourceName);
        Assert.Equal("https://fake.uspto.example/WD-40 Company", item.SourceUrl);

        // The verbatim spec-130 phrase + count in Title/RawText (the extractor contract).
        Assert.Equal(
            "Trademark activity (recent filings) — 3 trademark applications filed by 'WD-40 Company' "
                + "in the last 365 days",
            item.Title);
        Assert.Equal(
            "Owner 'WD-40 Company': 3 trademark applications filed since 2025-07-23, as of "
                + "2026-07-23T12:00:00.0000000+00:00. Signal: trademark activity (recent filings).",
            item.RawText);

        // NO raw mark texts in Title/RawText — "ROLLS OUT PRO" would trip the rolls-out/new-platform rules.
        Assert.DoesNotContain("rolls out", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rolls out", item.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blue works", item.RawText, StringComparison.OrdinalIgnoreCase);

        // Both instants are the injected TimeProvider's UTC now (a window snapshot has no publish date).
        Assert.Equal(FixedNow, item.PublishedAt);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + High quality; the count is the accrued history slice B reads.
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal(WdfcToken, item.Metadata["trademarkFeedUrl"]);
        Assert.Equal("WD-40 Company", item.Metadata["owner"]);
        Assert.Equal("3", item.Metadata["filingCount"]);
        Assert.Equal("365", item.Metadata["lookbackDays"]);
        Assert.Equal("2025-07-23", item.Metadata["filingFloor"]);
        Assert.Equal("41", item.Metadata["apiReportedTotal"]);
        Assert.Equal(
            "97000001: ROLLS OUT PRO | 97000002: BLUE WORKS | 97000003: SPECIALIST",
            item.Metadata["sampleMarks"]);
        Assert.Equal("2026-07-23T12:00:00.0000000+00:00", item.Metadata["retrievedAtUtc"]);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["WDFC"], item.CompanyHints);

        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
    }

    [Fact]
    public async Task CollectAsync_FilingFloorIsNowMinusLookbackDays()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), WdfcId, "WD-40 — Trademarks", WdfcToken);
        var reader = new FakeTrademarkSearchReader
        {
            ["WD-40 Company"] = new TrademarkSearchResult(1, 1, [new TrademarkFiling("1", "X", new DateOnly(2026, 6, 1))]),
        };
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [feed]);
        var options = new TrademarkCollectorOptions { LookbackDays = 90 };

        await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        // now (2026-07-23) − 90 days = 2026-04-24.
        Assert.Equal(new DateOnly(2026, 4, 24), reader.LastFilingFloor);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxSampleMarks()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), WdfcId, "WD-40 — Trademarks", WdfcToken);
        var reader = new FakeTrademarkSearchReader
        {
            ["WD-40 Company"] = new TrademarkSearchResult(
                3, 3,
                [
                    new TrademarkFiling("1", "Alpha", new DateOnly(2026, 6, 1)),
                    new TrademarkFiling("2", "Beta", new DateOnly(2026, 5, 1)),
                    new TrademarkFiling("3", "Gamma", new DateOnly(2026, 4, 1)),
                ]),
        };
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [feed]);
        var options = new TrademarkCollectorOptions { MaxSampleMarks = 2 };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("1: Alpha | 2: Beta", item.Metadata["sampleMarks"]);
        // The count still reflects the whole page, not the bounded sample.
        Assert.Equal("3", item.Metadata["filingCount"]);
    }

    [Fact]
    public async Task CollectAsync_ZeroFilings_IsAValidSuccessSnapshot()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), WdfcId, "WD-40 — Trademarks", WdfcToken);
        var reader = new FakeTrademarkSearchReader
        {
            ["WD-40 Company"] = new TrademarkSearchResult(0, 0, []),
        };
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Contains("0 trademark applications", item.Title, StringComparison.Ordinal);
        Assert.Equal("0", item.Metadata["filingCount"]);
        Assert.Equal(string.Empty, item.Metadata["sampleMarks"]);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), WdfcId, "WD-40 — Trademarks", "not-a-valid-token");
        var reader = new FakeTrademarkSearchReader();
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [feed]);

        var logger = new CapturingLogger<TrademarkActivityCollector>();
        var result = await CreateCollector(reader, logger: logger).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("malformed trademark feed token", failure.Reason);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CollectAsync_MissingApiKey_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"), WdfcId, "WD-40 — Trademarks", WdfcToken);
        var reader = new FakeTrademarkSearchReader();
        reader.SetFailure("WD-40 Company", TrademarkSearchOutcome.MissingApiKey, "API-key env var not set");
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("API-key env var not set", failure.Reason);
        Assert.Equal(WdfcToken, failure.SourceUrl);
    }

    [Fact]
    public async Task CollectAsync_ReaderFailure_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), WdfcId, "WD-40 — Trademarks", WdfcToken);
        var reader = new FakeTrademarkSearchReader();
        reader.SetFailure("WD-40 Company", TrademarkSearchOutcome.HttpError, "HTTP 403");
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal("HTTP 403", Assert.Single(result.Summary.Failures).Reason);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), WdfcId, "WD-40 — Trademarks", WdfcToken);
        var reader = new FakeTrademarkSearchReader
        {
            ["WD-40 Company"] = new TrademarkSearchResult(1, 1, [new TrademarkFiling("1", "X", new DateOnly(2026, 6, 1))]),
        };
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Equal(["WD-40 Company"], Assert.Single(result.Evidence).CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_NoTrademarkFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var rssFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), WdfcId, "WD-40 RSS",
            "https://wdfc.test/rss", feedType: "rss");
        var reader = new FakeTrademarkSearchReader();
        var context = new CollectionContext([Company(WdfcId, "WD-40 Company", "WDFC")], [rssFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, result.Summary.SourcesChecked);
    }

    [Fact]
    public async Task CollectAsync_MixedFeeds_CountsSummaryAndPreservesDeterministicOrder()
    {
        // WdfcId < HrlId, so FeedsOfType orders WD-40's feed first regardless of list order.
        var hrlFeed = Feed(
            Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), HrlId, "Hormel — Trademarks", HrlToken);
        var wdfcFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), WdfcId, "WD-40 — Trademarks", WdfcToken);

        var reader = new FakeTrademarkSearchReader
        {
            ["WD-40 Company"] = new TrademarkSearchResult(2, 2,
                [new TrademarkFiling("1", "A", new DateOnly(2026, 6, 1)), new TrademarkFiling("2", "B", new DateOnly(2026, 5, 1))]),
        };
        // The Hormel read fails, exercising the failed-count path alongside a successful feed.
        reader.SetFailure("Hormel Foods Corporation", TrademarkSearchOutcome.Timeout, "request timed out");

        var context = new CollectionContext(
            [Company(WdfcId, "WD-40 Company", "WDFC"), Company(HrlId, "Hormel Foods", "HRL")],
            [hrlFeed, wdfcFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("WD-40 — Trademarks", item.SourceName);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Hormel — Trademarks", failure.SourceName);
        Assert.Equal("request timed out", failure.Reason);
    }

    /// <summary>
    /// A fake reader keyed by owner name. <see cref="QueryUrl"/> returns a deterministic fake URL so the
    /// SourceUrl provenance assertion has a stable value; <see cref="LastFilingFloor"/> captures the computed
    /// window floor.
    /// </summary>
    private sealed class FakeTrademarkSearchReader : ITrademarkSearchReader
    {
        private readonly Dictionary<string, TrademarkSearchReadResult> _byOwner = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public DateOnly LastFilingFloor { get; private set; }

        public TrademarkSearchResult this[string owner]
        {
            set => _byOwner[owner] = TrademarkSearchReadResult.Success(value);
        }

        public void SetFailure(string owner, TrademarkSearchOutcome outcome, string detail) =>
            _byOwner[owner] = TrademarkSearchReadResult.Failure(outcome, detail);

        public string QueryUrl(string ownerName, DateOnly filingFloor) =>
            $"https://fake.uspto.example/{ownerName}";

        public Task<TrademarkSearchReadResult> ReadAsync(
            string ownerName, DateOnly filingFloor, CancellationToken ct)
        {
            ReadCount++;
            LastFilingFloor = filingFloor;
            return Task.FromResult(
                _byOwner.TryGetValue(ownerName, out var result)
                    ? result
                    : TrademarkSearchReadResult.Success(new TrademarkSearchResult(0, 0, [])));
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
