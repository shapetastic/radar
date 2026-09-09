using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Acquisitions;
using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.Acquisitions;

/// <summary>
/// SPEC 217 §1 — the recognition pass. Every assertion here is about the CLAUDE.md rule the slice exists to
/// honour twice over: nothing is discarded without being counted, and a write that did not land is never
/// reported as stored.
/// </summary>
public sealed class AcquisitionRecognitionPassTests
{
    private static readonly Guid CompanyId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Company Company = new(
        CompanyId,
        "MarineMax, Inc.",
        LegalName: null,
        Ticker: "HZO",
        Exchange: "NYSE",
        CountryCode: "US",
        Sector: null,
        Industry: null,
        Status: CompanyStatus.Active,
        CreatedAtUtc: DateTimeOffset.UnixEpoch,
        UpdatedAtUtc: DateTimeOffset.UnixEpoch,
        Themes: []);

    private static EvidenceItem Filing(string accession, string items = "1.01,7.01,9.01") => new(
        Id: Guid.NewGuid(),
        SourceType: EvidenceSourceType.Filing,
        SourceName: "MarineMax, Inc. — SEC filings (EDGAR)",
        SourceUrl:
            $"https://www.sec.gov/Archives/edgar/data/1057060/000119312526341302/{accession}-index.htm",
        Title: $"8-K — 8-K (2026-08-10) [items: {items}] Items: Entry into a Material Definitive Agreement.",
        Summary: null,
        RawText: "8-K",
        ContentHash: accession,
        PublishedAtUtc: new DateTimeOffset(2026, 8, 10, 8, 0, 12, TimeSpan.Zero),
        CollectedAtUtc: new DateTimeOffset(2026, 8, 10, 23, 48, 49, TimeSpan.Zero),
        MetadataJson: EvidenceMetadata.Compose(
            new Dictionary<string, string>
            {
                ["form"] = "8-K",
                ["items"] = items,
                ["accessionNumber"] = accession,
                ["primaryDocument"] = "d135056d8k.htm",
            },
            ["HZO"]),
        Quality: EvidenceQuality.High);

    private static AcquisitionRecognitionPass Pass(
        IEvidenceRepository evidence,
        IAcquisitionFilingBodyReader reader,
        IAcquisitionStore store,
        IAcquisitionScanCache cache,
        int budget = 40) =>
        new(
            evidence,
            new FixedResolver(CompanyId),
            reader,
            store,
            cache,
            new AcquisitionRecognitionOptions { MaxFetchesPerRun = budget },
            NullLogger<AcquisitionRecognitionPass>.Instance);

    [Fact]
    public async Task RecognisesTheMergerFiling_AndPersistsIt()
    {
        var filing = Filing("0001193125-26-341302");
        var store = new FakeStore();
        var cache = new FakeCache();
        var result = await Pass(
                new FakeEvidence([filing]),
                new FakeReader(AcquisitionFilingBodyFor(filing)),
                store,
                cache)
            .RunAsync([Company], default);

        Assert.Equal(1, result.Item101Filings);
        var record = Assert.Single(result.Recognised);
        Assert.Equal("0001193125-26-341302", record.Accession);
        Assert.Equal(filing.Id, record.EvidenceId);
        Assert.Equal(filing.PublishedAtUtc, record.AnnouncedOnUtc);
        Assert.Equal(AcquisitionAgreementScan.Version, record.ScanVersion);
        Assert.Equal(AcquisitionVerification.Verbatim, record.Verification);
        Assert.Equal(0, result.NotPersisted);
        Assert.Single(store.Written);

        // Authoritative answers are cached so the filing is never re-fetched.
        Assert.Single(cache.Entries);
    }

    [Fact]
    public async Task AFailedDurableWrite_IsCountedAsALoss_NeverAsARecognition()
    {
        var filing = Filing("0001193125-26-341302");
        var result = await Pass(
                new FakeEvidence([filing]),
                new FakeReader(AcquisitionFilingBodyFor(filing)),
                new FakeStore { FailWrites = true },
                new FakeCache())
            .RunAsync([Company], default);

        Assert.Empty(result.Recognised);
        Assert.Equal(1, result.NotPersisted);
        Assert.Equal(1, result.ByOutcome[AcquisitionScanOutcome.Recognised]);
    }

    [Fact]
    public async Task ACachedAnswer_IsReplayed_AndNeverRefetched()
    {
        var filing = Filing("0001193125-26-290439");
        var cache = new FakeCache();
        await cache.SetAsync(
            new AcquisitionScanCacheRecord(
                "0001193125-26-290439",
                CompanyId,
                AcquisitionScanOutcome.NoMergerAgreement,
                AcquisitionAgreementScan.Version,
                DateTimeOffset.UnixEpoch),
            default);

        var reader = new FakeReader(AcquisitionFilingBody.Failed("must not be called"));
        var result = await Pass(new FakeEvidence([filing]), reader, new FakeStore(), cache)
            .RunAsync([Company], default);

        Assert.Equal(0, reader.Calls);
        Assert.Equal(1, result.AlreadyScanned);
        Assert.Equal(1, result.ByOutcome[AcquisitionScanOutcome.NoMergerAgreement]);
    }

    [Fact]
    public async Task AnEntryFromAnotherScanVersion_IsAMiss_AndTheFilingIsRescanned()
    {
        var filing = Filing("0001193125-26-341302");
        var cache = new FakeCache();
        await cache.SetAsync(
            new AcquisitionScanCacheRecord(
                "0001193125-26-341302",
                CompanyId,
                AcquisitionScanOutcome.NoMergerAgreement,
                "acqscan-v0",
                DateTimeOffset.UnixEpoch),
            default);

        var reader = new FakeReader(AcquisitionFilingBodyFor(filing));
        var result = await Pass(new FakeEvidence([filing]), reader, new FakeStore(), cache)
            .RunAsync([Company], default);

        Assert.Equal(1, reader.Calls);
        Assert.Equal(0, result.AlreadyScanned);
        Assert.Single(result.Recognised);
    }

    [Fact]
    public async Task AFetchFailure_IsCounted_AndCachesNothing()
    {
        // A transient block must never permanently suppress a filing (the spec-114 rule).
        var filing = Filing("0001193125-26-341302");
        var cache = new FakeCache();
        var result = await Pass(
                new FakeEvidence([filing]),
                new FakeReader(AcquisitionFilingBody.Failed("HTTP 429")),
                new FakeStore(),
                cache)
            .RunAsync([Company], default);

        Assert.Equal(1, result.FetchFailed);
        Assert.Empty(result.Recognised);
        Assert.Empty(result.ByOutcome);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public async Task AnEmptyBodyRead_IsNotCached_SoALaterRunReattemptsIt()
    {
        var filing = Filing("0001193125-26-341302");
        var cache = new FakeCache();
        var result = await Pass(
                new FakeEvidence([filing]),
                new FakeReader(AcquisitionFilingBody.Success("too short")),
                new FakeStore(),
                cache)
            .RunAsync([Company], default);

        Assert.Equal(1, result.ByOutcome[AcquisitionScanOutcome.EmptyBody]);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public async Task TheFetchBudget_LeavesACOUNTEDBacklog_NeverAnInvisibleOne()
    {
        var filings = Enumerable.Range(0, 5)
            .Select(i => Filing($"0001193125-26-34130{i}"))
            .ToList();

        var result = await Pass(
                new FakeEvidence(filings),
                new FakeReader(AcquisitionFilingBody.Failed("blocked")),
                new FakeStore(),
                new FakeCache(),
                budget: 2)
            .RunAsync([Company], default);

        Assert.Equal(5, result.Item101Filings);
        Assert.Equal(2, result.FetchFailed);
        Assert.Equal(3, result.FetchBudgetRemaining);
    }

    [Fact]
    public async Task NonItem101Filings_AreNeverFetched()
    {
        var earningsOnly = Filing("0001193125-26-000001", items: "2.02,9.01");
        var reader = new FakeReader(AcquisitionFilingBody.Failed("must not be called"));

        var result = await Pass(new FakeEvidence([earningsOnly]), reader, new FakeStore(), new FakeCache())
            .RunAsync([Company], default);

        Assert.Equal(0, result.Item101Filings);
        Assert.Equal(0, reader.Calls);
    }

    private static AcquisitionFilingBody AcquisitionFilingBodyFor(EvidenceItem filing) =>
        filing.ContentHash == "0001193125-26-341302"
            ? AcquisitionFilingBody.Success(AcquisitionFilingFixtures.MarineMaxMerger)
            : AcquisitionFilingBody.Success(AcquisitionFilingFixtures.MarineMaxCreditAgreement);

    // ---- fakes -----------------------------------------------------------------------------------------

    private sealed class FakeEvidence(IReadOnlyList<EvidenceItem> items) : IEvidenceRepository
    {
        public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct) => Task.FromResult(true);

        public Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(items.FirstOrDefault(i => i.Id == id));

        public Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
            Task.FromResult(items.FirstOrDefault(i => i.ContentHash == contentHash));

        public Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(items);
    }

    private sealed class FixedResolver(Guid? companyId) : ICompanyResolver
    {
        public Task<CompanyResolutionResult> ResolveAsync(string mentionText, CancellationToken ct) =>
            ResolveAsync(mentionText, [], ct);

        public Task<CompanyResolutionResult> ResolveAsync(
            string mentionText, IReadOnlyList<string> companyHints, CancellationToken ct) =>
            Task.FromResult(new CompanyResolutionResult(companyId, companyId is null ? 0m : 1m, "test", null));
    }

    private sealed class FakeReader(AcquisitionFilingBody body) : IAcquisitionFilingBodyReader
    {
        public int Calls { get; private set; }

        public Task<AcquisitionFilingBody> ReadAsync(
            string cik, string accession, string? primaryDocument, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(body);
        }
    }

    private sealed class FakeStore : IAcquisitionStore
    {
        public bool FailWrites { get; init; }

        public List<PendingAcquisitionRecord> Written { get; } = [];

        public Task<DurableWriteResult> WriteIfNewAsync(
            PendingAcquisitionRecord record, CancellationToken ct)
        {
            if (FailWrites)
            {
                return Task.FromResult(DurableWriteResult.NotPersisted("path"));
            }

            Written.Add(record);
            return Task.FromResult(DurableWriteResult.Succeeded("path"));
        }

        public Task<AcquisitionStoreReadResult> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(new AcquisitionStoreReadResult(Written, 0));

        public Task<bool> ExistsAsync(Guid companyId, string accession, CancellationToken ct) =>
            Task.FromResult(Written.Any(r => r.CompanyId == companyId && r.Accession == accession));
    }

    private sealed class FakeCache : IAcquisitionScanCache
    {
        public Dictionary<string, AcquisitionScanCacheRecord> Entries { get; } = new(StringComparer.Ordinal);

        public Task<AcquisitionScanCacheRecord?> TryGetAsync(string accession, CancellationToken ct) =>
            Task.FromResult(Entries.GetValueOrDefault(accession));

        public Task<bool> SetAsync(AcquisitionScanCacheRecord record, CancellationToken ct)
        {
            Entries[record.Accession] = record;
            return Task.FromResult(true);
        }
    }
}
