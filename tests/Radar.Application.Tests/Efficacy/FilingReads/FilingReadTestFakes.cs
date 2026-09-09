using System.Globalization;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Efficacy.FilingReads;
using Radar.Application.EntityResolution;
using Radar.Application.Filings;
using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Application.Prices;
using Radar.Application.SignalExtraction;
using Radar.Application.Storage;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;

namespace Radar.Application.Tests.Efficacy.FilingReads;

/// <summary>Offline fakes + builders for the spec-218 filing-read measurement tests (no disk, no network).</summary>
internal static class FilingReadTestFakes
{
    public static readonly Guid CompanyId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static Company Company(string ticker = "POWL", string name = "Powell Industries") => new(
        CompanyId,
        name,
        LegalName: null,
        Ticker: ticker,
        Exchange: "NASDAQ",
        CountryCode: "US",
        Sector: null,
        Industry: null,
        Status: CompanyStatus.Active,
        CreatedAtUtc: DateTimeOffset.UnixEpoch,
        UpdatedAtUtc: DateTimeOffset.UnixEpoch,
        Themes: []);

    public static EvidenceItem FilingEvidence(
        string accession,
        DateOnly publishedDate,
        string title = "8-K (2026-08-03) [items: 2.02,8.01] Results of Operations",
        string rawText = "accession 0001-26-000001 form 8-K items 2.02,8.01",
        string? summary = null,
        string ticker = "POWL",
        // Explicit when a test needs the deterministic (published desc, id asc) ORDER to be pinned — e.g.
        // proving that a same-accession sibling could win an arbitrary single-record pick.
        Guid? id = null) => new(
        Id: id ?? Guid.NewGuid(),
        SourceType: EvidenceSourceType.Filing,
        SourceName: "sec-edgar",
        SourceUrl: "https://www.sec.gov/Archives/x",
        Title: title,
        Summary: summary,
        RawText: rawText,
        ContentHash: accession,
        PublishedAtUtc: new DateTimeOffset(
            publishedDate.Year, publishedDate.Month, publishedDate.Day, 12, 0, 0, TimeSpan.Zero),
        CollectedAtUtc: DateTimeOffset.UnixEpoch,
        Quality: EvidenceQuality.PrimarySource,
        MetadataJson: EvidenceMetadata.Compose(
            // The SAME keys the SEC filing collector writes, so the §2 structural-header classifier is
            // exercised against a production-shaped envelope rather than a thinner test one.
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["quality"] = "High",
                ["form"] = "8-K",
                ["items"] = "2.02,8.01",
                ["accessionNumber"] = accession,
                ["filingDate"] = publishedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            [ticker]));

    public static AnalyzedFilingRecord Directional(
        string accession,
        string direction = "Positive",
        decimal confidence = 0.95m,
        string excerpt = "8-K (2026-08-03) [items: 2.02,8.01] Results of Operations",
        string reason = "Revenue rose 38 to 60.",
        int cacheVersion = AnalyzedFilingRecord.CurrentCacheVersion,
        DateTimeOffset? observedAt = null,
        ComparabilityMarkers? markers = null) => new(
        accession,
        AnalyzedFilingOutcome.DirectionalSignalProduced,
        new ExtractedSignal(
            CompanyMention: "Powell Industries — SEC",
            SignalType: "GuidanceChange",
            Direction: direction,
            Strength: 8,
            Novelty: 6,
            Confidence: confidence,
            SupportingExcerpt: excerpt,
            Reason: reason),
        observedAt,
        cacheVersion,
        ComparabilityPolicy: markers is null ? null : "cmpscan-v1;cap=0.8",
        ComparabilityMarkers: markers);

    public static AnalyzedFilingRecord NoSignal(
        string accession,
        FilingNoSignalCause? cause,
        decimal? readConfidence = null,
        int cacheVersion = AnalyzedFilingRecord.CurrentCacheVersion) => new(
        accession,
        AnalyzedFilingOutcome.NoDirectionalSignal,
        Signal: null,
        ObservedAtUtc: null,
        CacheVersion: cacheVersion,
        NoSignalCause: cause,
        ReadConfidence: readConfidence);

    public static NewsTypingRecord Typing(
        Guid observationId, Guid? companyId, DateTimeOffset createdAt, params string[] statements) => new(
        SchemaVersion: NewsTypingRecord.CurrentSchemaVersion,
        TypingId: Guid.NewGuid(),
        RunId: null,
        ObservationId: observationId,
        PayloadHash: "hash",
        CompanyId: companyId,
        Ticker: "POWL",
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        ReaderName: "test",
        Provider: "test",
        ModelId: "test",
        PromptVersion: "v1",
        ResultSchemaVersion: "v1",
        TaxonomyVersion: NewsEventTaxonomy.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        CohortKey: "cohort",
        Relevance: null,
        DerivedPrimaryType: null,
        Facts: statements.Select(s => new NewsTypingValidatedFact(
            FactId: Guid.NewGuid(),
            EventTypes: [NewsEventType.MarketReaction],
            Statement: s,
            TemporalScope: null,
            Attribution: NewsFactAttribution.Company,
            AssertionStatus: NewsFactAssertionStatus.Reported,
            Confidence: 0.9,
            Citations: [])).ToList(),
        FactsTotal: statements.Length,
        FactsAccepted: statements.Length,
        FactsDropped: 0,
        FactDropReasons: [],
        Status: NewsTypingStatus.Typed,
        RawResponseHash: null,
        FailureDetail: null,
        Limits: new NewsTypingLimitsRecord(10, 7, null, null),
        ReusedFromTypingId: null,
        CreatedAtUtc: createdAt);

    public static NewsObservationRecord Observation(Guid observationId, DateTimeOffset? publishedAt) => new(
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        ObservationId: observationId,
        CompanyId: CompanyId,
        Ticker: "POWL",
        Collector: "newssearch",
        QueryPhrase: null,
        FeedId: null,
        FeedName: null,
        GoogleLandingUrl: "https://news.example/x",
        Publisher: "Example",
        PublisherSiteUrl: null,
        Headline: "Headline",
        DescriptionRaw: null,
        DescriptionText: null,
        DescriptionTruncated: false,
        PublishedAtUtc: publishedAt,
        RetrievedAtUtc: DateTimeOffset.UnixEpoch,
        FirstObservedAtUtc: DateTimeOffset.UnixEpoch,
        PayloadHash: "hash",
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        ArticleFetch: null);

    public static DirectionalFilingReadReporter Reporter(
        IAnalyzedFilingReadCorpus? corpus,
        IReadOnlyList<EvidenceItem>? evidence = null,
        IReadOnlyList<Company>? companies = null,
        IReadOnlyList<NewsTypingRecord>? typings = null,
        IReadOnlyList<NewsObservationRecord>? observations = null,
        IPriceHistoryStore? prices = null,
        bool registerNewsStores = true)
    {
        var repository = new FakeFilingReadCompanyRepository(companies ?? [Company()]);
        return new DirectionalFilingReadReporter(
            new FakeFilingReadEvidenceRepository(evidence ?? []),
            repository,
            new CompanyResolver(repository, NullLogger<CompanyResolver>.Instance),
            prices ?? new FakeFilingReadPriceStore(),
            NullLogger<DirectionalFilingReadReporter>.Instance,
            corpus,
            registerNewsStores ? new FakeNewsTypingStore(typings ?? []) : null,
            registerNewsStores ? new FakeNewsObservationArchive(observations ?? []) : null);
    }
}

/// <summary>A corpus seam returning a fixed enumeration result.</summary>
internal sealed class FakeAnalyzedFilingReadCorpus(AnalyzedFilingCorpus corpus) : IAnalyzedFilingReadCorpus
{
    public static FakeAnalyzedFilingReadCorpus Of(params AnalyzedFilingRecord[] records) =>
        new(new AnalyzedFilingCorpus(
            Entries: records
                .Select(r => new AnalyzedFilingCorpusEntry(r, r.Accession + ".json"))
                .ToList(),
            ModelSegment: "test-segment",
            CorpusDirectoryExists: true,
            EnumerationFailed: false,
            FilesScanned: records.Length,
            UnreadableOrUnparseableFiles: 0,
            OutsideCurrentModelSegmentFiles: 0,
            FileNameAccessionMismatchFiles: 0,
            OutcomeSignalMismatchFiles: 0));

    public Task<AnalyzedFilingCorpus> ReadAllAsync(CancellationToken ct) => Task.FromResult(corpus);
}

internal sealed class FakeFilingReadEvidenceRepository(IReadOnlyList<EvidenceItem> items) : IEvidenceRepository
{
    public Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct) => Task.FromResult(items);

    public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct) =>
        throw new NotSupportedException("The measurement is read-only over evidence.");

    public Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeFilingReadCompanyRepository(IReadOnlyList<Company> companies) : ICompanyRepository
{
    public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct) => Task.FromResult(companies);

    public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CompanyAlias>>([]);

    public Task AddAsync(Company company, CancellationToken ct) => throw new NotSupportedException();

    public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct) => throw new NotSupportedException();

    public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeNewsTypingStore(IReadOnlyList<NewsTypingRecord> records) : INewsTypingStore
{
    public Task<IReadOnlyList<NewsTypingRecord>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult(records);

    public Task<bool> WriteAsync(NewsTypingRecord record, CancellationToken ct) =>
        throw new NotSupportedException("The measurement is read-only over typings.");

    public Task<NewsTypingRecord?> FindCompletedAsync(
        string cohortKey, Guid observationId, string payloadHash, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeNewsObservationArchive(IReadOnlyList<NewsObservationRecord> records)
    : INewsObservationArchive
{
    public Task<IReadOnlyList<NewsObservationRecord>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult(records);

    public Task<NewsObservationWriteOutcome> WriteAsync(
        NewsObservationRecord record, CancellationToken ct) =>
        throw new NotSupportedException("The measurement is read-only over the archive.");

    public Task<bool> WriteBatchAsync(NewsObservationBatch batch, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class FakeFilingReadPriceStore : IPriceHistoryStore
{
    private readonly Dictionary<string, PriceHistory> _byTicker = new(StringComparer.OrdinalIgnoreCase);

    public FakeFilingReadPriceStore With(string ticker, params PriceBar[] bars)
    {
        _byTicker[ticker] = new PriceHistory(ticker, "test", DateTimeOffset.UnixEpoch, bars);
        return this;
    }

    public Task<PriceHistory?> ReadAsync(string ticker, CancellationToken ct) =>
        Task.FromResult(_byTicker.TryGetValue(ticker, out var history) ? history : null);

    public Task<DurableWriteResult> WriteAsync(PriceHistory history, CancellationToken ct) =>
        throw new NotSupportedException("The measurement is read-only over price (AD-14).");
}

/// <summary>Records the artifacts it is asked to write, and can fail every write on demand.</summary>
internal sealed class RecordingDirectionalFilingReadArtifactStore(bool failWrites = false)
    : IDirectionalFilingReadArtifactStore
{
    public List<(string Json, string Csv, string Markdown)> Written { get; } = [];

    public Task<DirectionalFilingReadArtifactPaths> WriteAsync(
        string json, string csv, string markdown, CancellationToken ct)
    {
        Written.Add((json, csv, markdown));
        return Task.FromResult(new DirectionalFilingReadArtifactPaths(
            DurableWriteResult.From("x.json", !failWrites),
            DurableWriteResult.From("x.csv", !failWrites),
            DurableWriteResult.From("x.md", !failWrites)));
    }
}
