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
/// Spec 215 §1 as CORRECTED BY SPEC 216 §2 — the collection pass files a FRESH earnings read's verified
/// metrics in the reported-metrics ledger under the company the signal RESOLVED to, with content-derived
/// ids, and reports every outcome in ONE aggregated line.
/// <para>
/// <b>The ORDERING is what this file exists to pin.</b> analysis → company resolved → OUTBOX ENVELOPE
/// WRITTEN (durable) → only then the analyzed-filing record stamped <c>reportedMetricsPolicy</c> → ledger
/// write from the envelope → acknowledge. Under spec 215 the stamp was applied at ANALYSIS time, so a
/// failed ledger write (or an unresolvable company) left the cache saying "extraction done" and the
/// metrics were lost permanently — counted, but lost. Every branch of the new ordering is pinned here:
/// a NotPersisted write leaves the envelope pending with a PERSISTED attempt count and the next pass
/// replays the byte-identical payload with no read at all; a durable write acknowledges; an
/// unresolved-company envelope is retried through resolution and re-enqueued once it succeeds; the third
/// attempt warns exactly once; and a cache stamp with no envelope behind it FAILS CLOSED.
/// </para>
/// </summary>
public sealed class CollectionPassReportedMetricsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Filed = new(2026, 9, 3, 20, 5, 0, TimeSpan.Zero);
    private const string CompanyName = "Argan, Inc.";
    private const string Accession = "0000100591-26-000011";
    private const string LedgerLinePrefix = "Reported-metrics ledger";

    private static VerifiedReportedMetrics Verified() => new(
        [
            new ReportedMetricReading(ReportedMetric.Revenue, "384.0", "million", "Q2 FY27", "227.0", "prior-year quarter", "Revenues of $384.0 million"),
            new ReportedMetricReading(ReportedMetric.Backlog, "2.518", "billion", "as of July 31, 2026", null, null, "Project backlog of $2.518 billion"),
        ],
        DroppedUnrecognised: 1,
        DroppedUnverified: 2,
        DroppedDuplicate: 1,
        PriorPairsDroppedIncomplete: 3,
        DroppedMetricNotInQuote: 4,
        DroppedPeriodNotInQuote: 5,
        DroppedFragment: 6,
        DroppedNotAssociated: 7);

    private sealed record Harness(
        CollectionPass Pass,
        RecordingLedger Ledger,
        RecordingOutbox Outbox,
        FakeAnalyzedFilingCache Cache,
        Guid CompanyId,
        CapturingLogger Log,
        List<string> Trace)
    {
        public (LogLevel Level, string Message) LedgerLine =>
            Log.Entries.Last(e => e.Message.StartsWith(LedgerLinePrefix, StringComparison.Ordinal));
    }

    private static async Task<Harness> BuildAsync(
        Func<EvidenceItem, DirectionalFilingSignal>? directional,
        bool registerLedger = true,
        string mention = CompanyName,
        IReadOnlyList<string>? hints = null,
        RecordingOutbox? outbox = null,
        FakeAnalyzedFilingCache? cache = null,
        bool seedCompany = true,
        bool collectEvidence = true)
    {
        var companies = new InMemoryCompanyRepository();
        var companyId = Guid.NewGuid();
        if (seedCompany)
        {
            await companies.AddAsync(
                new CompanyBuilder().WithId(companyId).WithName(CompanyName).WithTicker("AGX").Build(), default);
        }
        else
        {
            // The pass refuses to run with no companies at all; seed an unrelated one so resolution of the
            // filing's mention genuinely fails.
            await companies.AddAsync(
                new CompanyBuilder().WithName("Somebody Else Inc").WithTicker("SEI").Build(), default);
        }

        var evidence = new InMemoryEvidenceRepository();
        // ONE ordered trace across the outbox, the cache and the ledger: the spec-216 §2 claim is about
        // the ORDER of those three, so three separate lists could not express it.
        var trace = new List<string>();
        var ledger = new RecordingLedger(trace);
        var log = new CapturingLogger();
        outbox ??= new RecordingOutbox();
        outbox.Trace = trace;
        cache ??= new FakeAnalyzedFilingCache();
        cache.Trace = trace;

        var pass = new CollectionPass(
            [new FilingCollector(hints ?? ["AGX"], collectEvidence)],
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
            directional is null
                ? null
                : new FakeDirectionalSource(ev => directional(ev) with
                {
                    Signal = directional(ev).Signal with { CompanyMention = mention },
                }),
            reportedMetricStore: registerLedger ? ledger : null,
            reportedMetricOutbox: registerLedger ? outbox : null,
            analyzedFilingCache: cache);
        return new Harness(pass, ledger, outbox, cache, companyId, log, trace);
    }

    private static DirectionalFilingSignal Fresh(EvidenceItem ev, VerifiedReportedMetrics? metrics = null) => new(
        new ExtractedSignal(CompanyName, "GuidanceChange", "Positive", 8, 6, 0.9m, ev.Title, "Revenue rose."),
        ev,
        new ReportedMetricExtraction(Accession, "8-K", "openai:deepseek", metrics ?? Verified()),
        Accession: Accession);

    [Fact]
    public async Task AFreshExtraction_IsEnqueued_ThenStamped_ThenFiled_AndReportedOnce()
    {
        var h = await BuildAsync(ev => Fresh(ev));

        var result = await h.Pass.RunAsync(default);

        Assert.Equal(1, result.SignalsExtracted);
        var write = Assert.Single(h.Ledger.Writes);
        Assert.Equal(h.CompanyId, write.CompanyId);
        Assert.Equal(Accession, write.Accession);
        Assert.Equal(ReportedMetricsPolicy.Version, write.Policy);
        Assert.Equal(2, write.Records.Count);
        var revenue = write.Records[0];
        Assert.Equal(
            ReportedMetricRecord.IdentityFor(
                Accession, ReportedMetric.Revenue, "Q2 FY27", ReportedMetricsPolicy.Version),
            revenue.Id);
        Assert.Equal(h.CompanyId, revenue.CompanyId);
        Assert.Equal(Filed, revenue.FilingDateUtc);
        Assert.Equal("8-K", revenue.Form);
        Assert.Equal("227.0", revenue.PriorValue);
        Assert.Equal("openai:deepseek", revenue.ReaderIdentity);
        Assert.Equal(ReportedMetricVerification.Verbatim, revenue.Verification);
        Assert.Equal(ReportedMetricsPolicy.Version, revenue.Policy);
        Assert.NotEqual(Guid.Empty, revenue.EvidenceId);

        // SPEC 216 §2 — the ORDER, which is the whole point. The envelope is durable BEFORE the cache is
        // stamped, so the stamp means exactly "an outbox envelope exists for this accession under this
        // policy" and can never outlive the payload it claims.
        Assert.Equal(["enqueue", "stamp", "ledger-write", "acknowledge"], h.Trace);
        Assert.Equal(ReportedMetricsPolicy.Version, h.Cache.Entries[Accession].ReportedMetricsPolicy);
        Assert.Empty(await h.Outbox.EnumeratePendingAsync(default));
        Assert.Single(h.Outbox.Acknowledged);

        var line = h.LedgerLine;
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("1 fresh earnings read(s) extracted metrics", line.Message, StringComparison.Ordinal);
        Assert.Contains("ledger files written 1 / already on disk 0 / not persisted 0 / no resolved company 0 / no ledger registered 0", line.Message, StringComparison.Ordinal);
        Assert.Contains("records written 2", line.Message, StringComparison.Ordinal);
        Assert.Contains(
            "metrics dropped unverified 2 / unrecognised 1 / duplicate 1 / metric-not-in-quote 4 / "
                + "period-not-in-quote 5 / fragment 6 / not-associated 7; prior pairs dropped incomplete 3",
            line.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Outbox pending 0 (of which not acknowledged 0) / replayed 0 / acknowledged 1 / "
                + "attempts-exhausted 0 / not enqueued 0 / unresolved company 0 / attempt updates not "
                + "persisted 0 / cache stamps written (verified by re-read) 1 / not written 0; policy "
                + "stamp with no envelope 0 / not checked 0",
            line.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedEnqueue_StampsNothing_SoTheFilingSimplyReAnalyzesNextRun()
    {
        // THE PRECONDITION. If the envelope is not durable, nothing downstream happened — and crucially the
        // cache is NOT stamped, so the next run re-analyzes exactly as any uncached read does. Nothing was
        // persisted to lose.
        var h = await BuildAsync(ev => Fresh(ev), outbox: new RecordingOutbox { FailEnqueue = true });

        await h.Pass.RunAsync(default);

        Assert.Empty(h.Ledger.Writes);
        Assert.Null(h.Cache.Entries[Accession].ReportedMetricsPolicy);
        Assert.Contains("not enqueued 1", h.LedgerLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedLedgerWrite_LeavesThePayloadPending_WithOneRecordedAttempt()
    {
        var h = await BuildAsync(ev => Fresh(ev));
        h.Ledger.NextOutcome = DurableWriteOutcome.Failed;

        await h.Pass.RunAsync(default);

        var pending = Assert.Single(await h.Outbox.EnumeratePendingAsync(default));
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(FixedNow, pending.LastAttemptAtUtc);
        Assert.Equal(ReportedMetricOutboxState.Pending, pending.State);

        // The cache IS stamped: the payload is durable, which is what the stamp asserts. The LOSS the spec
        // closes is the one where the stamp outlived the payload, not the one where both survive.
        Assert.Equal(ReportedMetricsPolicy.Version, h.Cache.Entries[Accession].ReportedMetricsPolicy);
        Assert.Contains("not persisted 1", h.LedgerLine.Message, StringComparison.Ordinal);
        Assert.Contains("Outbox pending 1", h.LedgerLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNextPass_ReplaysThePendingEnvelope_ByteIdentically_WithNoReadAtAll()
    {
        var first = await BuildAsync(ev => Fresh(ev));
        first.Ledger.NextOutcome = DurableWriteOutcome.Failed;
        await first.Pass.RunAsync(default);
        var attempted = Assert.Single(first.Ledger.Writes);

        // The replay pass has NO directional source and collects NO filing evidence: there is nothing to
        // fetch and nothing to analyze. The envelope alone carries everything the ledger record needs.
        var replay = await BuildAsync(
            directional: null, outbox: first.Outbox, cache: first.Cache, collectEvidence: false);

        await replay.Pass.RunAsync(default);

        var replayed = Assert.Single(replay.Ledger.Writes);
        Assert.Equal(attempted.CompanyId, replayed.CompanyId);
        Assert.Equal(attempted.Accession, replayed.Accession);
        Assert.Equal(attempted.Policy, replayed.Policy);
        Assert.Equal(
            attempted.Records.Select(r => (r.Id, r.Metric, r.Value, r.Period, r.PriorValue, r.EvidenceId, r.FilingDateUtc, r.ReaderIdentity)),
            replayed.Records.Select(r => (r.Id, r.Metric, r.Value, r.Period, r.PriorValue, r.EvidenceId, r.FilingDateUtc, r.ReaderIdentity)));

        Assert.Empty(await replay.Outbox.EnumeratePendingAsync(default));
        Assert.Contains("replayed 1", replay.LedgerLine.Message, StringComparison.Ordinal);
        Assert.Contains("acknowledged 1", replay.LedgerLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheThirdFailedAttempt_WarnsExactlyOnce_FromThePersistedCount()
    {
        var outbox = new RecordingOutbox();
        var cache = new FakeAnalyzedFilingCache();

        for (var pass = 1; pass <= 3; pass++)
        {
            var h = pass == 1
                ? await BuildAsync(ev => Fresh(ev), outbox: outbox, cache: cache)
                : await BuildAsync(directional: null, outbox: outbox, cache: cache, collectEvidence: false);
            h.Ledger.NextOutcome = DurableWriteOutcome.Failed;
            await h.Pass.RunAsync(default);

            var warnings = h.Log.Entries
                .Where(e => e.Level == LogLevel.Warning
                    && e.Message.Contains("has now failed", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(pass == 3 ? 1 : 0, warnings.Count);
            if (pass == 3)
            {
                // The count is read from the PERSISTED envelope, so it survives restarts.
                Assert.Contains("failed 3 ledger attempt(s)", warnings[0].Message, StringComparison.Ordinal);
                Assert.Contains("attempts-exhausted 1", h.LedgerLine.Message, StringComparison.Ordinal);
            }
        }

        Assert.Equal(3, Assert.Single(await outbox.EnumeratePendingAsync(default)).Attempts);
    }

    [Fact]
    public async Task AnUnresolvedCompany_IsRoutable_AndIsReEnqueuedOnceResolutionSucceeds()
    {
        // SPEC 216 §2: an envelope whose company could not be resolved is written with a NULL company and
        // re-run through resolution on every replay — resolution may succeed later (a universe addition, a
        // hint fix). It is counted, never lost, and never guessed.
        var outbox = new RecordingOutbox();
        var cache = new FakeAnalyzedFilingCache();
        var first = await BuildAsync(
            ev => Fresh(ev), mention: CompanyName, hints: [], outbox: outbox, cache: cache, seedCompany: false);

        await first.Pass.RunAsync(default);

        Assert.Empty(first.Ledger.Writes);
        var unresolved = Assert.Single(await outbox.EnumeratePendingAsync(default));
        Assert.Null(unresolved.CompanyId);
        Assert.Equal(CompanyName, unresolved.CompanyMention);
        Assert.Contains("no resolved company 1", first.LedgerLine.Message, StringComparison.Ordinal);
        Assert.Contains("unresolved company 1", first.LedgerLine.Message, StringComparison.Ordinal);

        // The company now exists, so the replay resolves it, re-enqueues under the company, acknowledges
        // the unresolved copy and files the ledger.
        var second = await BuildAsync(
            directional: null, outbox: outbox, cache: cache, collectEvidence: false);

        await second.Pass.RunAsync(default);

        var write = Assert.Single(second.Ledger.Writes);
        Assert.Equal(second.CompanyId, write.CompanyId);
        Assert.Empty(await outbox.EnumeratePendingAsync(default));
        Assert.Contains("unresolved company 1", second.LedgerLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStampThatDidNotLand_IsCountedNotWritten_NeverAssumedWritten()
    {
        // The stamp is MEASURED, not assumed. IAnalyzedFilingCache.PutAsync returns a bare Task and the
        // file cache discards its writer's bool, so a gracefully-degraded disk write is indistinguishable
        // from a successful one AT THE CALL. The pass therefore re-reads the record and counts a stamp only
        // when the policy is actually there - otherwise "cache stamps written 1 / not written 0" would be a
        // claim about a write that never happened (the discarded-bool defect of specs 192/193).
        var cache = new FakeAnalyzedFilingCache { FailPut = true };
        var h = await BuildAsync(ev => Fresh(ev), cache: cache);

        await h.Pass.RunAsync(default);

        // The stamp is the only casualty: the envelope was durable first, so the ledger still fills.
        Assert.Single(h.Ledger.Writes);
        Assert.Null(cache.Entries[Accession].ReportedMetricsPolicy);
        Assert.Contains(
            "cache stamps written (verified by re-read) 0 / not written 1",
            h.LedgerLine.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedAcknowledge_IsCountedOnItsOwnAxis_AndLeavesTheEnvelopePending()
    {
        // The records are durable, so nothing is lost - but the envelope could not be moved to the
        // acknowledged area and will be replayed (the replay is idempotent: AlreadyAvailable). That is a
        // pending envelope AND an acknowledge failure, and both are counted rather than one hiding the
        // other.
        var outbox = new RecordingOutbox { FailAcknowledge = true };
        var h = await BuildAsync(ev => Fresh(ev), outbox: outbox);

        await h.Pass.RunAsync(default);

        Assert.Single(h.Ledger.Writes);
        Assert.Single(await outbox.EnumeratePendingAsync(default));
        // The rendered line DISCLOSES the containment, so a reader cannot mistake these for 2 envelopes.
        Assert.Contains(
            "Outbox pending 1 (of which not acknowledged 1) /",
            h.LedgerLine.Message,
            StringComparison.Ordinal);
        Assert.Contains("/ acknowledged 0 /", h.LedgerLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACacheStampWithNoEnvelope_FailsClosed_AndIsNamedOncePerAccession()
    {
        // SPEC 216 §2 — the 215-era shape: the extraction was made and lost before the ledger. It is NOT
        // acknowledged as an empty ledger, it is NOT auto-re-analyzed (recovery is a conscious maintainer
        // action), and it is COUNTED. Measured 2026-09-08 on the live cache: 500 records, ZERO carrying a
        // policy stamp of any kind.
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[Accession] = Record(ReportedMetricsPolicy.Version);
        var h = await BuildAsync(
            ev => Fresh(ev) with { ReportedMetrics = null, CachedReportedMetricsPolicy = ReportedMetricsPolicy.Version },
            cache: cache);

        await h.Pass.RunAsync(default);

        Assert.Empty(h.Ledger.Writes);
        Assert.Contains("policy stamp with no envelope 1", h.LedgerLine.Message, StringComparison.Ordinal);
        var warning = Assert.Single(
            h.Log.Entries,
            e => e.Message.Contains("claims reported-metrics policy", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(Accession, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACacheStampWithAnEnvelope_IsSilent()
    {
        var outbox = new RecordingOutbox();
        var cache = new FakeAnalyzedFilingCache();
        cache.Entries[Accession] = Record(ReportedMetricsPolicy.Version);
        await outbox.EnqueueAsync(Envelope(Guid.NewGuid()) with { State = ReportedMetricOutboxState.Acknowledged }, default);
        outbox.Acknowledge(Accession);

        var h = await BuildAsync(
            ev => Fresh(ev) with { ReportedMetrics = null, CachedReportedMetricsPolicy = ReportedMetricsPolicy.Version },
            outbox: outbox,
            cache: cache);

        await h.Pass.RunAsync(default);

        Assert.Contains("policy stamp with no envelope 0", h.LedgerLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACacheReplay_WithANullPolicy_WritesNothing_ButTheRunStillReportsAMeasuredZero()
    {
        var h = await BuildAsync(ev => Fresh(ev) with { ReportedMetrics = null });

        await h.Pass.RunAsync(default);

        Assert.Empty(h.Ledger.Writes);
        Assert.Contains("0 fresh earnings read(s) extracted metrics", h.LedgerLine.Message, StringComparison.Ordinal);
        Assert.Contains("policy stamp with no envelope 0", h.LedgerLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlreadyFiledAccession_IsCountedOnItsOwnAxis_AndStillAcknowledges()
    {
        var h = await BuildAsync(ev => Fresh(ev));
        h.Ledger.NextOutcome = DurableWriteOutcome.AlreadyAvailable;

        await h.Pass.RunAsync(default);

        Assert.Contains("written 0 / already on disk 1 / not persisted 0", h.LedgerLine.Message, StringComparison.Ordinal);
        Assert.Contains("records written 0", h.LedgerLine.Message, StringComparison.Ordinal);
        Assert.Empty(await h.Outbox.EnumeratePendingAsync(default));
    }

    [Fact]
    public async Task NoLedgerRegistered_IsCounted_AndOtherwiseByteIdentical()
    {
        var h = await BuildAsync(ev => Fresh(ev), registerLedger: false);

        var result = await h.Pass.RunAsync(default);

        Assert.Equal(1, result.SignalsExtracted);
        Assert.Empty(h.Ledger.Writes);
        Assert.Contains("no ledger registered 1", h.LedgerLine.Message, StringComparison.Ordinal);
        Assert.Contains(
            "metrics dropped unverified 2 / unrecognised 1 / duplicate 1 / metric-not-in-quote 4 / "
                + "period-not-in-quote 5 / fragment 6 / not-associated 7; prior pairs dropped incomplete 3",
            h.LedgerLine.Message,
            StringComparison.Ordinal);

        // Nothing is stamped when there is nowhere to file: the stamp asserts an envelope exists.
        Assert.Null(h.Cache.Entries[Accession].ReportedMetricsPolicy);
    }

    // ---------------------------------------------------------------- doubles

    private static AnalyzedFilingRecord Record(string? policy) => new(
        Accession,
        AnalyzedFilingOutcome.DirectionalSignalProduced,
        new ExtractedSignal(CompanyName, "GuidanceChange", "Positive", 8, 6, 0.9m, "t", "cached"),
        Filed,
        AnalyzedFilingRecord.CurrentCacheVersion,
        ReportedMetricsPolicy: policy);

    private static ReportedMetricOutboxEnvelope Envelope(Guid companyId) => new(
        OutboxId: ReportedMetricOutboxEnvelope.IdentityFor(ReportedMetricsPolicy.Version, Accession),
        Policy: ReportedMetricsPolicy.Version,
        CompanyId: companyId,
        CompanyMention: CompanyName,
        CompanyHints: [],
        EvidenceId: Guid.NewGuid(),
        Accession: Accession,
        Form: "8-K",
        FilingDateUtc: Filed,
        ReaderIdentity: "openai:deepseek",
        Metrics: Verified().Verified,
        DroppedUnrecognised: 0,
        DroppedUnverified: 0,
        DroppedDuplicate: 0,
        PriorPairsDroppedIncomplete: 0,
        DroppedMetricNotInQuote: 0,
        DroppedPeriodNotInQuote: 0,
        DroppedFragment: 0,
        DroppedNotAssociated: 0,
        State: ReportedMetricOutboxState.Pending,
        Attempts: 0,
        CreatedAtUtc: FixedNow,
        LastAttemptAtUtc: null);

    private sealed class FilingCollector(IReadOnlyList<string> hints, bool collect) : IEvidenceCollector
    {
        public string CollectorName => "sec";

        public EvidenceSourceType SourceType => EvidenceSourceType.Filing;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(new CollectionResult(
                collect
                    ?
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
                    ]
                    : [],
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

    private sealed class RecordingLedger(List<string>? trace = null) : IReportedMetricStore
    {
        public List<(Guid CompanyId, string Accession, string Policy, IReadOnlyList<ReportedMetricRecord> Records)> Writes { get; } = [];

        public DurableWriteOutcome NextOutcome { get; set; } = DurableWriteOutcome.Written;

        public Task<DurableWriteResult> WriteIfNewAsync(
            Guid companyId,
            string accession,
            string policy,
            IReadOnlyList<ReportedMetricRecord> records,
            CancellationToken ct)
        {
            trace?.Add("ledger-write");
            Writes.Add((companyId, accession, policy, records));
            return Task.FromResult(new DurableWriteResult("(ledger)", NextOutcome));
        }

        public Task<IReadOnlyList<ReportedMetricRecord>> GetForCompanyAsync(Guid companyId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReportedMetricRecord>>([]);
    }

    /// <summary>
    /// An in-memory <see cref="IReportedMetricOutbox"/> with the file store's semantics: an envelope is
    /// keyed by (policy, accession) WITHIN its area (pending/unresolved), acknowledged copies are KEPT,
    /// and the attempt count is persisted on the stored envelope.
    /// </summary>
    private sealed class RecordingOutbox : IReportedMetricOutbox
    {
        private readonly Dictionary<string, ReportedMetricOutboxEnvelope> _pending = new(StringComparer.Ordinal);

        public bool FailEnqueue { get; set; }

        /// <summary>A durable acknowledge that could not be recorded: the envelope stays pending.</summary>
        public bool FailAcknowledge { get; set; }

        public List<string> Trace { get; set; } = [];

        public List<ReportedMetricOutboxEnvelope> Acknowledged { get; } = [];

        private static string Key(ReportedMetricOutboxEnvelope e) => Key(e.Policy, e.Accession, e.CompanyId is null);

        private static string Key(string policy, string accession, bool unresolved) =>
            $"{(unresolved ? "unresolved" : "pending")}|{policy}|{accession}";

        public Task<DurableWriteResult> EnqueueAsync(ReportedMetricOutboxEnvelope envelope, CancellationToken ct)
        {
            if (FailEnqueue)
            {
                return Task.FromResult(DurableWriteResult.NotPersisted("(outbox)"));
            }

            Trace.Add("enqueue");
            var key = Key(envelope);
            if (_pending.ContainsKey(key)
                || Acknowledged.Any(a => Key(a) == key))
            {
                return Task.FromResult(DurableWriteResult.AlreadyOnDisk("(outbox)"));
            }

            _pending[key] = envelope;
            return Task.FromResult(DurableWriteResult.Succeeded("(outbox)"));
        }

        public Task<IReadOnlyList<ReportedMetricOutboxEnvelope>> EnumeratePendingAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReportedMetricOutboxEnvelope>>(
                [
                    .. _pending.Values
                        .Where(e => e.State == ReportedMetricOutboxState.Pending)
                        .OrderBy(e => e.CompanyId?.ToString("D") ?? string.Empty, StringComparer.Ordinal)
                        .ThenBy(e => e.Accession, StringComparer.Ordinal),
                ]);

        public Task<ReportedMetricOutboxEnvelope?> MarkAttemptAsync(
            ReportedMetricOutboxEnvelope envelope, DateTimeOffset attemptedAtUtc, CancellationToken ct)
        {
            var updated = envelope with { Attempts = envelope.Attempts + 1, LastAttemptAtUtc = attemptedAtUtc };
            _pending[Key(updated)] = updated;
            return Task.FromResult<ReportedMetricOutboxEnvelope?>(updated);
        }

        public Task<bool> AcknowledgeAsync(ReportedMetricOutboxEnvelope envelope, CancellationToken ct)
        {
            Trace.Add("acknowledge");
            if (FailAcknowledge)
            {
                return Task.FromResult(false);
            }

            _pending.Remove(Key(envelope));
            Acknowledged.Add(envelope with { State = ReportedMetricOutboxState.Acknowledged });
            return Task.FromResult(true);
        }

        public void Acknowledge(string accession)
        {
            var key = Key(ReportedMetricsPolicy.Version, accession, unresolved: false);
            if (_pending.Remove(key, out var envelope))
            {
                Acknowledged.Add(envelope with { State = ReportedMetricOutboxState.Acknowledged });
            }
        }

        public Task<bool> ExistsAsync(string policy, string accession, CancellationToken ct) =>
            Task.FromResult(
                _pending.Keys.Any(k => k.EndsWith($"|{policy}|{accession}", StringComparison.Ordinal))
                || Acknowledged.Any(a =>
                    string.Equals(a.Policy, policy, StringComparison.Ordinal)
                    && string.Equals(a.Accession, accession, StringComparison.Ordinal)));
    }

    private sealed class FakeAnalyzedFilingCache : IAnalyzedFilingCache
    {
        public Dictionary<string, AnalyzedFilingRecord> Entries { get; } = new(StringComparer.Ordinal)
        {
            [Accession] = Record(policy: null),
        };

        public List<string> Trace { get; set; } = [];

        /// <summary>
        /// A gracefully-degraded disk write: PutAsync returns normally and nothing lands. This is exactly
        /// what FileAnalyzedFilingCache does when GracefulFileWriter fails, since PutAsync has no outcome
        /// to report - which is why the pass verifies the stamp by re-reading rather than assuming it.
        /// </summary>
        public bool FailPut { get; set; }

        public Task<AnalyzedFilingRecord?> TryGetAsync(string accession, CancellationToken ct) =>
            Task.FromResult(Entries.TryGetValue(accession, out var record) ? record : null);

        public Task PutAsync(AnalyzedFilingRecord record, CancellationToken ct)
        {
            Trace.Add("stamp");
            if (FailPut)
            {
                return Task.CompletedTask;
            }

            Entries[record.Accession] = record;
            return Task.CompletedTask;
        }
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
