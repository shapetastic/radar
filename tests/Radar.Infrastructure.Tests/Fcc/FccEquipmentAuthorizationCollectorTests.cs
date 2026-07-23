using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Infrastructure.Fcc;

namespace Radar.Infrastructure.Tests.Fcc;

public sealed class FccEquipmentAuthorizationCollectorTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MrcyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EriiId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string MrcyToken = "grantee=Mercury Systems, Inc.";
    private const string EriiToken = "grantee=Energy Recovery, Inc.";

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
        Guid id, Guid companyId, string name, string url, string feedType = "fccauth") =>
        new(id, companyId, feedType, name, url, FixedNow);

    private static FccEquipmentAuthorizationCollector CreateCollector(
        IFccAuthReader reader,
        FccCollectorOptions? options = null,
        ILogger<FccEquipmentAuthorizationCollector>? logger = null) =>
        new(
            reader,
            logger ?? NullLogger<FccEquipmentAuthorizationCollector>.Instance,
            new FixedTimeProvider(FixedNow),
            options ?? new FccCollectorOptions());

    [Fact]
    public async Task CollectAsync_MapsReadToEquipmentAuthorizationEvidenceWithProvenanceAndHints()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), MrcyId,
            "Mercury Systems — Recent FCC equipment authorizations (EAS)", MrcyToken);

        var reader = new FakeFccAuthReader
        {
            ["Mercury Systems, Inc."] = new FccAuthResult(
                GrantCount: 3,
                Grants:
                [
                    // A description that would trip 'launches' — must stay in metadata ONLY.
                    new EquipmentAuthorization("ABC111", "Wireless launch controller", new DateOnly(2026, 6, 1)),
                    new EquipmentAuthorization("DEF222", "Radiation-hardened module", new DateOnly(2026, 5, 2)),
                    new EquipmentAuthorization("GHI333", "Secure transmitter", new DateOnly(2026, 4, 3)),
                ]),
        };

        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);
        var item = Assert.Single(result.Evidence);

        Assert.Equal(EvidenceSourceType.EquipmentAuthorization, item.SourceType);
        Assert.Equal("Mercury Systems — Recent FCC equipment authorizations (EAS)", item.SourceName);
        Assert.Equal("https://fake.fcc.example/Mercury Systems, Inc.", item.SourceUrl);

        // The verbatim spec-128 phrase + count in Title/RawText (the extractor contract).
        Assert.Equal(
            "FCC equipment authorization (recent grants) — 3 authorizations granted to 'Mercury Systems, Inc.' "
                + "in the last 180 days",
            item.Title);
        Assert.Equal(
            "Grantee 'Mercury Systems, Inc.': 3 FCC equipment authorizations granted since 2026-01-24, as of "
                + "2026-07-23T12:00:00.0000000+00:00. Signal: fcc equipment authorization (recent grants).",
            item.RawText);

        // NO raw product descriptions in Title/RawText — "Wireless launch controller" would trip the launches rule.
        Assert.DoesNotContain("launch", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("launch", item.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transmitter", item.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ABC111", item.RawText, StringComparison.Ordinal);

        // Both instants are the injected TimeProvider's UTC now (a window snapshot has no publish date).
        Assert.Equal(FixedNow, item.PublishedAt);
        Assert.Equal(FixedNow, item.CollectedAt);

        // Provenance metadata + High quality; the count is the accrued history slice B reads.
        Assert.Equal("High", item.Metadata["quality"]);
        Assert.Equal(MrcyToken, item.Metadata["fccFeedUrl"]);
        Assert.Equal("Mercury Systems, Inc.", item.Metadata["grantee"]);
        Assert.Equal("3", item.Metadata["grantCount"]);
        Assert.Equal("180", item.Metadata["lookbackDays"]);
        Assert.Equal("2026-01-24", item.Metadata["grantFloor"]);
        Assert.Equal(
            "ABC111: Wireless launch controller | DEF222: Radiation-hardened module | GHI333: Secure transmitter",
            item.Metadata["sampleAuthorizations"]);
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
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), MrcyId, "Mercury — FCC", MrcyToken);
        var reader = new FakeFccAuthReader
        {
            ["Mercury Systems, Inc."] = new FccAuthResult(1, [new EquipmentAuthorization("X1", "Y", new DateOnly(2026, 6, 1))]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new FccCollectorOptions { LookbackDays = 90 };

        await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        // now (2026-07-23) − 90 days = 2026-04-24.
        Assert.Equal(new DateOnly(2026, 4, 24), reader.LastGrantFloor);
    }

    [Fact]
    public async Task CollectAsync_HonoursMaxSampleAuthorizations()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), MrcyId, "Mercury — FCC", MrcyToken);
        var reader = new FakeFccAuthReader
        {
            ["Mercury Systems, Inc."] = new FccAuthResult(
                3,
                [
                    new EquipmentAuthorization("A1", "Alpha", new DateOnly(2026, 6, 1)),
                    new EquipmentAuthorization("B2", "Beta", new DateOnly(2026, 5, 1)),
                    new EquipmentAuthorization("C3", "Gamma", new DateOnly(2026, 4, 1)),
                ]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);
        var options = new FccCollectorOptions { MaxSampleAuthorizations = 2 };

        var result = await CreateCollector(reader, options).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("A1: Alpha | B2: Beta", item.Metadata["sampleAuthorizations"]);
        // The count still reflects the whole page, not the bounded sample.
        Assert.Equal("3", item.Metadata["grantCount"]);
    }

    [Fact]
    public async Task CollectAsync_ZeroGrants_IsAValidSuccessSnapshot()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), MrcyId, "Mercury — FCC", MrcyToken);
        var reader = new FakeFccAuthReader
        {
            ["Mercury Systems, Inc."] = new FccAuthResult(0, []),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Contains("0 authorizations granted", item.Title, StringComparison.Ordinal);
        Assert.Equal("0", item.Metadata["grantCount"]);
        Assert.Equal(string.Empty, item.Metadata["sampleAuthorizations"]);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
    }

    [Fact]
    public async Task CollectAsync_MalformedFeedToken_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005"), MrcyId, "Mercury — FCC", "not-a-valid-token");
        var reader = new FakeFccAuthReader();
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var logger = new CapturingLogger<FccEquipmentAuthorizationCollector>();
        var result = await CreateCollector(reader, logger: logger).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(1, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("malformed fcc feed token", failure.Reason);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CollectAsync_ReaderFailure_DegradesToSourceFailureWithoutThrowing()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007"), MrcyId, "Mercury — FCC", MrcyToken);
        var reader = new FakeFccAuthReader();
        reader.SetFailure("Mercury Systems, Inc.", FccAuthOutcome.HttpError, "HTTP 403");
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", "MRCY")], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Summary.SourcesFailed);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("HTTP 403", failure.Reason);
        Assert.Equal(MrcyToken, failure.SourceUrl);
    }

    [Fact]
    public async Task CollectAsync_CompanyWithoutTicker_HintsUseName()
    {
        var feed = Feed(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008"), MrcyId, "Mercury — FCC", MrcyToken);
        var reader = new FakeFccAuthReader
        {
            ["Mercury Systems, Inc."] = new FccAuthResult(1, [new EquipmentAuthorization("X1", "Y", new DateOnly(2026, 6, 1))]),
        };
        var context = new CollectionContext([Company(MrcyId, "Mercury Systems", ticker: null)], [feed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        Assert.Equal(["Mercury Systems"], Assert.Single(result.Evidence).CompanyHints);
    }

    [Fact]
    public async Task CollectAsync_NoFccFeeds_ReturnsEmptyAndNeverCallsReader()
    {
        var rssFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009"), MrcyId, "Mercury RSS",
            "https://mrcy.test/rss", feedType: "rss");
        var reader = new FakeFccAuthReader();
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
            Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a"), EriiId, "Energy Recovery — FCC", EriiToken);
        var mrcyFeed = Feed(
            Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a"), MrcyId, "Mercury — FCC", MrcyToken);

        var reader = new FakeFccAuthReader
        {
            ["Mercury Systems, Inc."] = new FccAuthResult(2,
                [new EquipmentAuthorization("A1", "A", new DateOnly(2026, 6, 1)), new EquipmentAuthorization("B2", "B", new DateOnly(2026, 5, 1))]),
        };
        // The Energy Recovery read fails, exercising the failed-count path alongside a successful feed.
        reader.SetFailure("Energy Recovery, Inc.", FccAuthOutcome.Timeout, "request timed out");

        var context = new CollectionContext(
            [Company(MrcyId, "Mercury Systems", "MRCY"), Company(EriiId, "Energy Recovery", "ERII")],
            [eriiFeed, mrcyFeed]);

        var result = await CreateCollector(reader).CollectAsync(context, CancellationToken.None);

        var item = Assert.Single(result.Evidence);
        Assert.Equal("Mercury — FCC", item.SourceName);

        Assert.Equal(2, result.Summary.SourcesChecked);
        Assert.Equal(1, result.Summary.SourcesSucceeded);
        Assert.Equal(1, result.Summary.SourcesFailed);
        Assert.Equal(1, result.Summary.ItemsCollected);
        var failure = Assert.Single(result.Summary.Failures);
        Assert.Equal("Energy Recovery — FCC", failure.SourceName);
        Assert.Equal("request timed out", failure.Reason);
    }

    /// <summary>
    /// A fake reader keyed by grantee name. <see cref="QueryUrl"/> returns a deterministic fake URL so the
    /// SourceUrl provenance assertion has a stable value; <see cref="LastGrantFloor"/> captures the computed
    /// window floor.
    /// </summary>
    private sealed class FakeFccAuthReader : IFccAuthReader
    {
        private readonly Dictionary<string, FccAuthReadResult> _byGrantee = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public DateOnly LastGrantFloor { get; private set; }

        public FccAuthResult this[string grantee]
        {
            set => _byGrantee[grantee] = FccAuthReadResult.Success(value);
        }

        public void SetFailure(string grantee, FccAuthOutcome outcome, string detail) =>
            _byGrantee[grantee] = FccAuthReadResult.Failure(outcome, detail);

        public string QueryUrl(string granteeName, DateOnly grantFloor) =>
            $"https://fake.fcc.example/{granteeName}";

        public Task<FccAuthReadResult> ReadAsync(
            string granteeName, DateOnly grantFloor, CancellationToken ct)
        {
            ReadCount++;
            LastGrantFloor = grantFloor;
            return Task.FromResult(
                _byGrantee.TryGetValue(granteeName, out var result)
                    ? result
                    : FccAuthReadResult.Success(new FccAuthResult(0, [])));
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
