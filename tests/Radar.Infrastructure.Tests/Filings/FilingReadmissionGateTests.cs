using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.Filings;
using Radar.Application.Pipeline;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Filings;
using Radar.Domain.Signals;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Filings;

/// <summary>
/// SPEC 205 §1 — the two gates around a legacy v2 no-signal cache record, proven SEPARATELY, through the
/// REAL admission seam: a real <see cref="CollectionPass"/> feeding a real
/// <see cref="DirectionalFilingSignalSource"/> over a real (on-disk) <see cref="FileAnalyzedFilingCache"/>.
/// <list type="number">
/// <item>An ALREADY-DURABLE filing never enters the source at all — <c>CollectionPass</c> hands the source
/// only newly-stored evidence, so the accrued v2 no-signal record is NOT a migration queue and causes ZERO
/// analyzer/model calls (and zero www.sec.gov fetches). Mutation: feeding accrued evidence into the
/// candidate list makes the v2 no-signal record a genuine miss and the analyzer fires — this test fails.</item>
/// <item>The SAME accession genuinely re-admitted as new evidence reaches the source, where the v2
/// no-signal record is a MISS: exactly ONE current read happens and its successful write REPLACES the v2
/// file with a v3 record naming the cause. Mutation: turning v2 no-signal into a HIT replays nothing (a v2
/// record has no cause envelope), makes no call and writes no v3 — this test fails.</item>
/// </list>
/// The rest of the §1 matrix is already pinned elsewhere and is deliberately not repeated here: v2
/// produced-signal stays a HIT and 0/1/future versions stay misses (<see cref="FileAnalyzedFilingCacheTests"/>),
/// and a v3 no-signal record replays by its recorded cause (<see cref="DirectionalFilingSignalSourceTests"/>).
/// </summary>
public sealed class FilingReadmissionGateTests : IDisposable
{
    private const string Accession = "0001049521-26-000011";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _cacheDir;

    public FilingReadmissionGateTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "radar-readmission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    [Fact]
    public async Task AccruedFilingWithV2NoSignalRecord_NeverEntersTheSource_ZeroAnalyzerOrFetchCalls()
    {
        await PlantV2NoSignalRecordAsync();
        var fixture = CreateFixture();

        // Accrue the filing FIRST, exactly as an earlier run would have: the pass's own mapper computes the
        // same content-derived identity (spec 145), so the re-collection below is an AddIfNewAsync duplicate.
        await fixture.Evidence.AddIfNewAsync(
            fixture.Mapper.ToEvidenceItem(FilingEvidence()), CancellationToken.None);

        var result = await fixture.Pass.RunAsync(CancellationToken.None);

        // The admission gate held: the filing was re-collected but not re-admitted…
        Assert.Equal(1, result.EvidenceCollected);
        Assert.Equal(0, result.EvidenceNew);

        // …so the source received an EMPTY candidate list — the accrued filing never entered it —
        var batch = Assert.Single(fixture.Spy.CandidateBatches);
        Assert.Empty(batch);

        // …and the v2 no-signal record scheduled NO work of any kind: zero model calls, zero fetches.
        Assert.Equal(0, fixture.Analyzer.AnalyzeCount);
        Assert.Equal(0, fixture.Reader.ReadCount);

        // The v2 file itself is untouched (no rewrite outside ordinary successful analysis).
        var untouched = await ReadCacheFileAsync();
        Assert.Equal(2, untouched.CacheVersion);
        Assert.Null(untouched.NoSignalCause);
    }

    [Fact]
    public async Task ReadmittedAccession_ReachesTheSource_V2NoSignalIsAMiss_OneReadWritesV3()
    {
        await PlantV2NoSignalRecordAsync();
        var fixture = CreateFixture();

        // No accrued copy this time: the same accession is genuinely admitted as NEW evidence (e.g. an old
        // raw-evidence write failed, or the content now has a distinct content-derived identity).
        var result = await fixture.Pass.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.EvidenceCollected);
        Assert.Equal(1, result.EvidenceNew);

        // The candidate reached the source…
        var batch = Assert.Single(fixture.Spy.CandidateBatches);
        var candidate = Assert.Single(batch);
        Assert.Equal(EvidenceSourceType.Filing, candidate.SourceType);

        // …the v2 no-signal record was a MISS, so exactly ONE current read happened…
        Assert.Equal(1, fixture.Reader.ReadCount);
        Assert.Equal(1, fixture.Analyzer.AnalyzeCount);

        // …and the read (a confident Mixed) was emitted as the spec-204 read signal.
        var produced = Assert.Single(fixture.Spy.Produced);
        Assert.Equal("GuidanceChange", produced.Signal.SignalType);
        Assert.Equal("Mixed", produced.Signal.Direction);

        // The ordinary successful analysis replaced the v2 file in place with a v3 record naming the cause.
        var v3 = await ReadCacheFileAsync();
        Assert.Equal(AnalyzedFilingRecord.CurrentCacheVersion, v3.CacheVersion);
        Assert.Equal(AnalyzedFilingOutcome.NoDirectionalSignal, v3.Outcome);
        Assert.Equal(FilingNoSignalCause.Mixed, v3.NoSignalCause);
        Assert.Equal("Mixed", v3.ReadDirection);
        Assert.Equal(0.95m, v3.ReadConfidence);
        Assert.Equal("Both up and down.", v3.Rationale);
    }

    // ---------------------------------------------------------------------------------------------
    // The fixture: a real CollectionPass whose directional source is the REAL DirectionalFilingSignalSource
    // over the REAL on-disk FileAnalyzedFilingCache, wrapped in a candidate-recording spy.
    // ---------------------------------------------------------------------------------------------

    private sealed record GateFixture(
        CollectionPass Pass,
        SpyDirectionalFilingSignalSource Spy,
        FakeSecEarningsReleaseReader Reader,
        FakeFilingAnalyzer Analyzer,
        InMemoryEvidenceRepository Evidence,
        CollectedEvidenceMapper Mapper);

    private GateFixture CreateFixture()
    {
        var reader = new FakeSecEarningsReleaseReader(
            SecEarningsReleaseReadResult.Success(PlausibleBody("Some earnings text."), "EX-99.1", "ex991.htm"));
        // A confident Mixed read: on a genuine admission it yields the spec-204 read signal AND a v3
        // no-signal cache record naming the cause — both asserted by the re-admission test.
        var analyzer = new FakeFilingAnalyzer(new FilingSentiment(FilingDirection.Mixed, 0.95m, "Both up and down."));

        var source = new DirectionalFilingSignalSource(
            reader,
            analyzer,
            new FileAnalyzedFilingCache(
                new FileAnalyzedFilingCacheOptions { RootDirectory = _cacheDir },
                NullLogger<FileAnalyzedFilingCache>.Instance),
            new DirectionalFilingSignalOptions(),
            NullLogger<DirectionalFilingSignalSource>.Instance);
        var spy = new SpyDirectionalFilingSignalSource(source);

        var mapper = new CollectedEvidenceMapper(
            new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance);
        var evidence = new InMemoryEvidenceRepository();
        var companies = new InMemoryCompanyRepository();

        var pass = new CollectionPass(
            [new FakeCollector("sec", new CollectionResult([FilingEvidence()], CollectionSummary.Empty))],
            mapper,
            evidence,
            new NullRawStore(),
            new EmptyExtractor(),
            new CompanyResolver(companies, NullLogger<CompanyResolver>.Instance),
            new DeterministicSignalReviewer(
                new FixedTime(FixedNow), NullLogger<DeterministicSignalReviewer>.Instance),
            new InMemorySignalRepository(),
            new InMemorySignalReviewRepository(),
            new NullSignalFileStore(),
            companies,
            new CleanHealthValidator(),
            new FixedTime(FixedNow),
            NullLogger<CollectionPass>.Instance,
            new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default),
            directionalFilingSignals: spy);

        return new GateFixture(pass, spy, reader, analyzer, evidence, mapper);
    }

    /// <summary>
    /// The earnings 8-K exactly as the SEC collector would hand it over: form 8-K, item 2.02, a real index
    /// SourceUrl carrying CIK + dashed accession, and the collector-shaped metadata bag.
    /// </summary>
    private static CollectedEvidence FilingEvidence()
    {
        var accNoDashes = Accession.Replace("-", string.Empty, StringComparison.Ordinal);
        return new CollectedEvidence(
            SourceType: EvidenceSourceType.Filing,
            SourceName: "Mercury — SEC",
            SourceUrl: $"https://www.sec.gov/Archives/edgar/data/0001049521/{accNoDashes}/{Accession}-index.htm",
            Title: "8-K — Report (2026-08-28) [items: 2.02,9.01] Items: Results of Operations and Financial Condition.",
            RawText: $"8-K filing accession {Accession} filed 2026-08-28: Report.",
            PublishedAt: FixedNow.AddDays(-2),
            CollectedAt: FixedNow.AddMinutes(-5),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["quality"] = "High",
                ["form"] = "8-K",
                ["items"] = "2.02,9.01",
                ["accessionNumber"] = Accession,
            });
    }

    /// <summary>Writes a REAL pre-204 (v2) no-signal file where the cache reads for <see cref="Accession"/>.</summary>
    private async Task PlantV2NoSignalRecordAsync()
    {
        var v2 = new AnalyzedFilingRecord(
            Accession, AnalyzedFilingOutcome.NoDirectionalSignal, null, null, CacheVersion: 2);
        await File.WriteAllTextAsync(
            CacheFilePath(), JsonSerializer.Serialize(v2, RadarFileStoreJson.Options), CancellationToken.None);
    }

    private async Task<AnalyzedFilingRecord> ReadCacheFileAsync()
    {
        var record = JsonSerializer.Deserialize<AnalyzedFilingRecord>(
            await File.ReadAllTextAsync(CacheFilePath(), CancellationToken.None), RadarFileStoreJson.Options);
        Assert.NotNull(record);
        return record!;
    }

    private string CacheFilePath() => Path.Combine(_cacheDir, Accession.ToLowerInvariant() + ".json");

    /// <summary>Pads <paramref name="lead"/> past the source's minimum-plausible-body guard (spec 114).</summary>
    private static string PlausibleBody(string lead) =>
        lead + " " + string.Concat(Enumerable.Repeat(
            "Full results of operations, margin detail and cash-flow discussion follow in the release body. ", 4));

    /// <summary>Records every candidate batch the pass hands over, then delegates to the REAL source.</summary>
    private sealed class SpyDirectionalFilingSignalSource(IDirectionalFilingSignalSource inner)
        : IDirectionalFilingSignalSource
    {
        public List<IReadOnlyList<EvidenceItem>> CandidateBatches { get; } = [];

        public List<DirectionalFilingSignal> Produced { get; } = [];

        public string ScoringDescriptor() => inner.ScoringDescriptor();

        public async Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
            IReadOnlyList<EvidenceItem> candidateEvidence, DateTimeOffset asOfUtc, CancellationToken ct)
        {
            CandidateBatches.Add(candidateEvidence);
            var produced = await inner.ProduceAsync(candidateEvidence, asOfUtc, ct).ConfigureAwait(false);
            Produced.AddRange(produced);
            return produced;
        }
    }

    private sealed class FakeCollector(string name, CollectionResult result) : IEvidenceCollector
    {
        public string CollectorName => name;

        public EvidenceSourceType SourceType => EvidenceSourceType.Filing;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class EmptyExtractor : ISignalExtractor
    {
        public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(new ExtractSignalsOutput([], "none"));
    }

    private sealed class NullSignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("(null)"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    private sealed class NullRawStore : IRawEvidenceStore
    {
        // Spec 206 §3: Written — every item is newly durable, so admission flows exactly as before.
        public Task<Radar.Application.Storage.DurableWriteResult> WriteIfNewAsync(
            EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(Radar.Application.Storage.DurableWriteResult.Succeeded("(null-raw-store)"));
    }

    private sealed class CleanHealthValidator : ICollectionHealthValidator
    {
        public Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(CollectionHealthReport.Empty);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSecEarningsReleaseReader(SecEarningsReleaseReadResult result)
        : ISecEarningsReleaseReader
    {
        public int ReadCount { get; private set; }

        public Task<SecEarningsReleaseReadResult> ReadAsync(string cik, string accession, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeFilingAnalyzer(FilingSentiment sentiment) : IFilingAnalyzer
    {
        public int AnalyzeCount { get; private set; }

        public Task<FilingSentiment> AnalyzeAsync(string? earningsReleaseText, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AnalyzeCount++;
            return Task.FromResult(sentiment);
        }
    }
}
