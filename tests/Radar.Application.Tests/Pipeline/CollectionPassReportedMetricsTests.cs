using Microsoft.Extensions.Logging;
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
using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.Pipeline;

/// <summary>
/// Spec 215 §1 — the collection pass files a FRESH earnings read's verified metrics in the reported-metrics
/// ledger under the company the signal RESOLVED to, with content-derived ids, and reports every outcome in
/// ONE aggregated line: written / already on disk / not persisted / no resolved company / no ledger
/// registered, records written, and the three drop classes. A cache replay (null extraction) writes nothing;
/// an unregistered ledger is byte-identical behaviour plus a counted line.
/// </summary>
public sealed class CollectionPassReportedMetricsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Filed = new(2026, 9, 3, 20, 5, 0, TimeSpan.Zero);
    private const string CompanyName = "Argan, Inc.";
    private const string Accession = "0000100591-26-000011";

    private static VerifiedReportedMetrics Verified() => new(
        [
            new ReportedMetricReading(ReportedMetric.Revenue, "384.0", "million", "Q2 FY27", "227.0", "prior-year quarter", "Revenues of $384.0 million"),
            new ReportedMetricReading(ReportedMetric.Backlog, "2.518", "billion", "as of July 31, 2026", null, null, "Project backlog of $2.518 billion"),
        ],
        DroppedUnrecognised: 1,
        DroppedUnverified: 2,
        DroppedDuplicate: 1,
        PriorPairsDroppedIncomplete: 3);

    private static async Task<(CollectionPass Pass, RecordingLedger Ledger, Guid CompanyId, CapturingLogger Log)> BuildAsync(
        Func<EvidenceItem, DirectionalFilingSignal> directional,
        bool registerLedger = true,
        string mention = CompanyName,
        IReadOnlyList<string>? hints = null)
    {
        var companies = new InMemoryCompanyRepository();
        var companyId = Guid.NewGuid();
        await companies.AddAsync(
            new CompanyBuilder().WithId(companyId).WithName(CompanyName).WithTicker("AGX").Build(), default);
        var evidence = new InMemoryEvidenceRepository();
        var ledger = new RecordingLedger();
        var log = new CapturingLogger();

        var pass = new CollectionPass(
            [new FilingCollector(hints ?? ["AGX"])],
            new CollectedEvidenceMapper(new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance),
            evidence,
            new PassThroughRawEvidenceStore(evidence),
            new NoSignalExtractor(),
            new CompanyResolver(companies, NullLogger<CompanyResolver>.Instance),
            new DeterministicSignalReviewer(new FixedTimeProvider(FixedNow), NullLogger<DeterministicSignalReviewer>.Instance),
            new InMemorySignalRepository(),
            new InMemorySignalReviewRepository(),
            new NullSignalFileStore(),
            companies,
            new CleanHealthValidator(),
            new FixedTimeProvider(FixedNow),
            log,
            new AllGenuineWeights(),
            new FakeDirectionalSource(ev => directional(ev) with
            {
                Signal = directional(ev).Signal with { CompanyMention = mention },
            }),
            reportedMetricStore: registerLedger ? ledger : null);
        return (pass, ledger, companyId, log);
    }

    private static DirectionalFilingSignal Fresh(EvidenceItem ev, VerifiedReportedMetrics? metrics = null) => new(
        new ExtractedSignal(CompanyName, "GuidanceChange", "Positive", 8, 6, 0.9m, ev.Title, "Revenue rose."),
        ev,
        new ReportedMetricExtraction(Accession, "8-K", "openai:deepseek", metrics ?? Verified()));

    [Fact]
    public async Task AFreshExtraction_IsFiledUnderTheResolvedCompany_WithContentDerivedIds_AndReportedOnce()
    {
        var (pass, ledger, companyId, log) = await BuildAsync(ev => Fresh(ev));

        var result = await pass.RunAsync(default);

        Assert.Equal(1, result.SignalsExtracted);
        var write = Assert.Single(ledger.Writes);
        Assert.Equal(companyId, write.CompanyId);
        Assert.Equal(Accession, write.Accession);
        Assert.Equal(2, write.Records.Count);
        var revenue = write.Records[0];
        Assert.Equal(ReportedMetricRecord.IdentityFor(Accession, ReportedMetric.Revenue, "Q2 FY27"), revenue.Id);
        Assert.Equal(companyId, revenue.CompanyId);
        Assert.Equal(Filed, revenue.FilingDateUtc);
        Assert.Equal("8-K", revenue.Form);
        Assert.Equal("227.0", revenue.PriorValue);
        Assert.Equal("openai:deepseek", revenue.ReaderIdentity);
        Assert.Equal(ReportedMetricVerification.Verbatim, revenue.Verification);
        Assert.Equal(ReportedMetricsPolicy.Version, revenue.Policy);
        Assert.NotEqual(Guid.Empty, revenue.EvidenceId);

        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("Reported-metrics ledger", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("1 fresh earnings read(s) extracted metrics", line.Message, StringComparison.Ordinal);
        Assert.Contains("ledger files written 1 / already on disk 0 / not persisted 0 / no resolved company 0 / no ledger registered 0", line.Message, StringComparison.Ordinal);
        Assert.Contains("records written 2", line.Message, StringComparison.Ordinal);
        Assert.Contains("metrics dropped unverified 2 / unrecognised 1 / duplicate 1; prior pairs dropped incomplete 3", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlreadyFiledAccession_AndAFailedWrite_AreCountedOnTheirOwnAxes()
    {
        var (pass, ledger, _, log) = await BuildAsync(ev => Fresh(ev));
        ledger.NextOutcome = DurableWriteOutcome.AlreadyAvailable;
        await pass.RunAsync(default);
        var alreadyLine = log.Entries.Last(e => e.Message.StartsWith("Reported-metrics ledger", StringComparison.Ordinal));
        Assert.Contains("written 0 / already on disk 1 / not persisted 0", alreadyLine.Message, StringComparison.Ordinal);
        Assert.Contains("records written 0", alreadyLine.Message, StringComparison.Ordinal);

        var (failing, failingLedger, _, failingLog) = await BuildAsync(ev => Fresh(ev));
        failingLedger.NextOutcome = DurableWriteOutcome.Failed;
        await failing.RunAsync(default);
        var failedLine = failingLog.Entries.Last(e => e.Message.StartsWith("Reported-metrics ledger", StringComparison.Ordinal));
        Assert.Contains("written 0 / already on disk 0 / not persisted 1", failedLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACacheReplay_WritesNothing_ButTheRunStillReportsAMeasuredZero()
    {
        var (pass, ledger, _, log) = await BuildAsync(ev => Fresh(ev) with { ReportedMetrics = null });

        await pass.RunAsync(default);

        Assert.Empty(ledger.Writes);
        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("Reported-metrics ledger", StringComparison.Ordinal));
        Assert.Contains("0 fresh earnings read(s) extracted metrics", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnresolvedCompany_IsCounted_NeverGuessed()
    {
        // No collector hint and a mention nobody resolves: the resolver leaves CompanyId null.
        var (pass, ledger, _, log) = await BuildAsync(ev => Fresh(ev), mention: "Nobody Anyone Knows Ltd", hints: []);

        await pass.RunAsync(default);

        Assert.Empty(ledger.Writes);
        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("Reported-metrics ledger", StringComparison.Ordinal));
        Assert.Contains("no resolved company 1 / no ledger registered 0", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLedgerRegistered_IsCounted_AndOtherwiseByteIdentical()
    {
        var (pass, ledger, _, log) = await BuildAsync(ev => Fresh(ev), registerLedger: false);

        var result = await pass.RunAsync(default);

        Assert.Equal(1, result.SignalsExtracted);
        Assert.Empty(ledger.Writes);
        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("Reported-metrics ledger", StringComparison.Ordinal));
        Assert.Contains("no ledger registered 1", line.Message, StringComparison.Ordinal);
        Assert.Contains("metrics dropped unverified 2 / unrecognised 1 / duplicate 1; prior pairs dropped incomplete 3", line.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- doubles

    private sealed class FilingCollector(IReadOnlyList<string> hints) : IEvidenceCollector
    {
        public string CollectorName => "sec";

        public EvidenceSourceType SourceType => EvidenceSourceType.Filing;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(new CollectionResult(
                [
                    new CollectedEvidence(
                        SourceType: EvidenceSourceType.Filing,
                        SourceName: CompanyName,
                        SourceUrl: "https://www.sec.gov/Archives/edgar/data/100591/000010059126000011/0000100591-26-000011-index.htm",
                        Title: "8-K — Report (2026-09-03) [items: 2.02,9.01]",
                        RawText: "8-K filing accession 0000100591-26-000011 filed 2026-09-03: Report.",
                        PublishedAt: Filed,
                        CollectedAt: FixedNow,
                        Metadata: new Dictionary<string, string> { ["quality"] = "High", ["form"] = "8-K", ["items"] = "2.02,9.01" })
                    {
                        CompanyHints = hints,
                    },
                ],
                CollectionSummary.Empty));
    }

    /// <summary>
    /// The pass's raw-evidence seam as a SEPARATE object from the repository (the tests/in-memory
    /// composition): every write reports Written, and the pass then admits the item to the repository
    /// itself — so this double must not touch the repository, or the pass's own AddIfNewAsync would refuse
    /// the item as a duplicate of itself.
    /// </summary>
    private sealed class PassThroughRawEvidenceStore(InMemoryEvidenceRepository inner) : IRawEvidenceStore
    {
        public Task<DurableWriteResult> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct)
        {
            _ = inner;
            return Task.FromResult(DurableWriteResult.Succeeded("(raw)"));
        }
    }

    private sealed class NoSignalExtractor : ISignalExtractor
    {
        public Task<ExtractSignalsOutput> ExtractAsync(EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(new ExtractSignalsOutput([], "none"));
    }

    private sealed class FakeDirectionalSource(Func<EvidenceItem, DirectionalFilingSignal> produce) : IDirectionalFilingSignalSource
    {
        public Task<IReadOnlyList<DirectionalFilingSignal>> ProduceAsync(
            IReadOnlyList<EvidenceItem> candidateEvidence, DateTimeOffset asOfUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DirectionalFilingSignal>>([.. candidateEvidence.Select(produce)]);

        public string ScoringDescriptor() => "fake-directional";
    }

    private sealed class RecordingLedger : IReportedMetricStore
    {
        public List<(Guid CompanyId, string Accession, IReadOnlyList<ReportedMetricRecord> Records)> Writes { get; } = [];

        public DurableWriteOutcome NextOutcome { get; set; } = DurableWriteOutcome.Written;

        public Task<DurableWriteResult> WriteIfNewAsync(
            Guid companyId, string accession, IReadOnlyList<ReportedMetricRecord> records, CancellationToken ct)
        {
            Writes.Add((companyId, accession, records));
            return Task.FromResult(new DurableWriteResult("(ledger)", NextOutcome));
        }

        public Task<IReadOnlyList<ReportedMetricRecord>> GetForCompanyAsync(Guid companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReportedMetricRecord>>([]);
    }

    private sealed class NullSignalFileStore : ISignalFileStore
    {
        public Task<DurableWriteResult> WriteAsync(
            Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("(signal)"));

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc, DateTimeOffset knownAsOfUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    private sealed class CleanHealthValidator : ICollectionHealthValidator
    {
        public Task<CollectionHealthReport> ValidateAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(CollectionHealthReport.Empty);
    }

    private sealed class AllGenuineWeights : Radar.Application.Scoring.IAttentionSourceWeights
    {
        public Radar.Application.Scoring.AttentionSourceResolution Resolve(string? sourceName) =>
            Radar.Application.Scoring.AttentionSourceResolution.Unclassified(1.0, sourceName ?? string.Empty);

        public string CanonicalDescriptor() => "test-all-genuine";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingLogger : ILogger<CollectionPass>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
