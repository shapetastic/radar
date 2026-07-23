using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Fda;

namespace Radar.Infrastructure.Tests.Fda;

public sealed class FdaClearanceCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid AxgnId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TmdxId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string AxgnToken = "applicant=Axogen";
    private const string TmdxToken = "applicant=TransMedics";

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
        Guid id, Guid companyId, string name, string url, string feedType = "fda") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static FdaClearanceCollector CreateCollector(
        IFdaClearanceReader reader, FdaCollectorOptions? options = null, ILogger<FdaClearanceCollector>? logger = null) =>
        new(
            reader,
            logger ?? NullLogger<FdaClearanceCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new FdaCollectorOptions());

    [Fact]
    public async Task CollectAsync_MapsClearancesToRegulatoryApprovalEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), AxgnId,
            "Axogen — Recent FDA device clearances (openFDA)", AxgnToken);

        var reader = new FakeFdaClearanceReader
        {
            ["Axogen"] = new FdaClearanceResult(
                ClearanceCount: 3,
                Clearances:
                [
                    // A device name that would trip 'partnership' — must stay in metadata ONLY.
                    new FdaClearance("K250001", "Cardiac partnership system", new DateOnly(2026, 6, 1), "510(k)"),
                    new FdaClearance("K250002", "Nerve repair conduit", new DateOnly(2026, 5, 2), "510(k)"),
                    new FdaClearance("P250010", "Organ perfusion module", new DateOnly(2026, 4, 3), "PMA"),
                ],
                ReportedTotal510k: 8,
                ReportedTotalPma: 2),
        };

        var context = new CollectionContext([Company(AxgnId, "Axogen", "AXGN")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.RegulatoryApproval, item.SourceType);
        Assert.Equal("Axogen — Recent FDA device clearances (openFDA)", item.SourceName);
        Assert.Equal("https://fake.openfda.example/Axogen", item.SourceUrl);

        // The verbatim spec-129 phrase + count in Title/RawText (the extractor contract).
        Assert.Equal(
            "FDA clearance or approval (recent) — 3 device clearances/approvals for 'Axogen' "
                + "in the last 365 days",
            item.Title);
        Assert.Equal(
            "Applicant 'Axogen': 3 FDA device clearances/approvals since 2025-07-23, as of "
                + "2026-07-23T12:00:00.0000000+00:00. Signal: fda clearance or approval (recent).",
            item.RawText);

        // NO raw device names in Title/RawText — "Cardiac partnership system" would trip the partnership rule.
        Assert.DoesNotContain("partnership", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partnership", item.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("perfusion", item.RawText, StringComparison.OrdinalIgnoreCase);

        // Both instants are the injected TimeProvider's UTC now (a window snapshot has no publish date).
        Assert.Equal(FixedNow, item.PublishedAt);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + High quality.
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal(AxgnToken, item.Metadata["fdaFeedUrl"]);
        Assert.Equal("Axogen", item.Metadata["applicant"]);
        Assert.Equal("3", item.Metadata["clearanceCount"]);
        Assert.Equal("365", item.Metadata["lookbackDays"]);
        Assert.Equal("2025-07-23", item.Metadata["decisionFloor"]);
        Assert.Equal("8", item.Metadata["reportedTotal510k"]);
        Assert.Equal("2", item.Metadata["reportedTotalPma"]);
        Assert.Equal(
            "K250001 [510(k)]: Cardiac partnership system | K250002 [510(k)]: Nerve repair conduit "
                + "| P250010 [PMA]: Organ perfusion module",
            item.Metadata["sampleClearances"]);
        Assert.Equal("2026-07-23T12:00:00.0000000+00:00", item.Metadata["retrievedAtUtc"]);

        // Company hint comes from the feed binding (ticker preferred), never invented.
        Assert.Equal(["AXGN"], item.CompanyHints);

        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(0, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
    }

    [Fact]
    public async Task CollectAsync_DecisionFloorIsNowMinusLookbackDays()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), AxgnId, "Axogen — FDA", AxgnToken);
        var reader = new FakeFdaClearanceReader
        {
            ["Axogen"] = new FdaClearanceResult(1, [new FdaClearance("K1", "X", new DateOnly(2026, 6, 1), "510(k)")], 1, 0),
        };
        var context = new CollectionContext([Company(AxgnId, "Axogen", "AXGN")], [feed]);
        var options = new FdaCollectorOptions { LookbackDays = 90 };

        await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        // now (2026-07-23) − 90 days = 2026-04-24.
        Assert.Equal(new DateOnly(2026, 4, 24), reader.LastDecisionFloor);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxSampleClearances()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), AxgnId, "Axogen — FDA", AxgnToken);
        var reader = new FakeFdaClearanceReader
        {
            ["Axogen"] = new FdaClearanceResult(
                3,
                [
                    new FdaClearance("K1", "Alpha", new DateOnly(2026, 6, 1), "510(k)"),
                    new FdaClearance("K2", "Beta", new DateOnly(2026, 5, 1), "510(k)"),
                    new FdaClearance("P3", "Gamma", new DateOnly(2026, 4, 1), "PMA"),
                ],
                2, 1),
        };
        var context = new CollectionContext([Company(AxgnId, "Axogen", "AXGN")], [feed]);
        var options = new FdaCollectorOptions { MaxSampleClearances = 2 };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("K1 [510(k)]: Alpha | K2 [510(k)]: Beta", item.Metadata["sampleClearances"]);
        // The count still reflects the whole merged set, not the bounded sample.
        Assert.Equal("3", item.Metadata["clearanceCount"]);
    }

    [Fact]
    public async Task CollectAsync_ZeroClearances_IsAValidSuccessSnapshot()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), AxgnId, "Axogen — FDA", AxgnToken);
        var reader = new FakeFdaClearanceReader
        {
            ["Axogen"] = new FdaClearanceResult(0, [], 0, 0),
        };
        var context = new CollectionContext([Company(AxgnId, "Axogen", "AXGN")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Contains("0 device clearances/approvals", item.Title, StringComparison.Ordinal);
        Assert.Equal("0", item.Metadata["clearanceCount"]);
        Assert.Equal(string.Empty, item.Metadata["sampleClearances"]);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), AxgnId, "Axogen — FDA", "not-a-valid-token");
        var reader = new FakeFdaClearanceReader();
        var context = new CollectionContext([Company(AxgnId, "Axogen", "AXGN")], [feed]);

        var logger = new CapturingLogger<FdaClearanceCollector>();
        var result = await CreateCollector(reader, logger: logger).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("malformed fda feed token", failure.Reason);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CollectAsync_ReaderFailure_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), AxgnId, "Axogen — FDA", AxgnToken);
        var reader = new FakeFdaClearanceReader();
        reader.SetFailure("Axogen", FdaReadOutcome.HttpError, "HTTP 403");
        var context = new CollectionContext([Company(AxgnId, "Axogen", "AXGN")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("HTTP 403", failure.Reason);
        Assert.Equal(AxgnToken, failure.SourceUrl);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), AxgnId, "Axogen — FDA", AxgnToken);
        var reader = new FakeFdaClearanceReader
        {
            ["Axogen"] = new FdaClearanceResult(1, [new FdaClearance("K1", "X", new DateOnly(2026, 6, 1), "510(k)")], 1, 0),
        };
        var context = new CollectionContext([Company(AxgnId, "Axogen", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Equal(["Axogen"], Assert.Single(result.Evidence).CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_NoFdaFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var rssFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), AxgnId, "Axogen RSS",
            "https://axgn.test/rss", feedType: "rss");
        var reader = new FakeFdaClearanceReader();
        var context = new CollectionContext([Company(AxgnId, "Axogen", "AXGN")], [rssFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, result.Summary.SourcesChecked);
    }

    [Fact]
    public async Task CollectAsync_MixedFeeds_CountsSummaryAndPreservesDeterministicOrder()
    {
        // AxgnId < TmdxId, so FeedsOfType orders Axogen's feed first regardless of list order.
        var tmdxFeed = Feed(
            Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), TmdxId, "TransMedics — FDA", TmdxToken);
        var axgnFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), AxgnId, "Axogen — FDA", AxgnToken);

        var reader = new FakeFdaClearanceReader
        {
            ["Axogen"] = new FdaClearanceResult(2,
                [new FdaClearance("K1", "A", new DateOnly(2026, 6, 1), "510(k)"), new FdaClearance("P2", "B", new DateOnly(2026, 5, 1), "PMA")],
                1, 1),
        };
        // The TransMedics read fails, exercising the failed-count path alongside a successful feed.
        reader.SetFailure("TransMedics", FdaReadOutcome.Timeout, "request timed out");

        var context = new CollectionContext(
            [Company(AxgnId, "Axogen", "AXGN"), Company(TmdxId, "TransMedics", "TMDX")],
            [tmdxFeed, axgnFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Axogen — FDA", item.SourceName);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("TransMedics — FDA", failure.SourceName);
        Assert.Equal("request timed out", failure.Reason);
    }

    /// <summary>
    /// A fake reader keyed by applicant name. <see cref="QueryUrl"/> returns a deterministic fake URL so the
    /// SourceUrl provenance assertion has a stable value; <see cref="LastDecisionFloor"/> captures the computed
    /// window floor.
    /// </summary>
    private sealed class FakeFdaClearanceReader : IFdaClearanceReader
    {
        private readonly Dictionary<string, FdaClearanceReadResult> _byApplicant = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public DateOnly LastDecisionFloor { get; private set; }

        public FdaClearanceResult this[string applicant]
        {
            set => _byApplicant[applicant] = FdaClearanceReadResult.Success(value);
        }

        public void SetFailure(string applicant, FdaReadOutcome outcome, string detail) =>
            _byApplicant[applicant] = FdaClearanceReadResult.Failure(outcome, detail);

        public string QueryUrl(string applicantName, DateOnly decisionFloor) =>
            $"https://fake.openfda.example/{applicantName}";

        public Task<FdaClearanceReadResult> ReadAsync(
            string applicantName, DateOnly decisionFloor, CancellationToken ct)
        {
            ReadCount++;
            LastDecisionFloor = decisionFloor;
            return Task.FromResult(
                _byApplicant.TryGetValue(applicantName, out var result)
                    ? result
                    : FdaClearanceReadResult.Success(new FdaClearanceResult(0, [], 0, 0)));
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
