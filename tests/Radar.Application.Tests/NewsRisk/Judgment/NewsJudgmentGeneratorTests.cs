using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Tests.Ai;
using Radar.Application.Tests.NewsRisk;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 185 §5 — the judgment pass at the generator boundary: the EOSE end-to-end chain (typed facts →
/// deterministic family collapse → judge → durable record with full provenance → policy-derived marker),
/// the facts-only request (ONE entry per family, however syndicated), the no-model-call
/// <c>InsufficientFacts</c> rule, the completed-judgment cache, and the fail-closed no-stage-1 path.
/// </summary>
public sealed class NewsJudgmentGeneratorTests
{
    private static readonly Guid RunId = Guid.Parse("12121212-3434-5656-7878-909090909090");
    private static readonly Guid Eose = Guid.Parse("e05ee05e-e05e-e05e-e05e-e05ee05ee05e");

    private sealed class InMemoryJudgmentStore : INewsJudgmentStore
    {
        public List<NewsJudgmentRecord> Written { get; } = [];
        public List<NewsJudgmentRecord> Seed { get; } = [];

        public Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct)
        {
            Written.Add(record);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NewsJudgmentRecord>>([.. Seed, .. Written]);

        public Task<NewsJudgmentRecord?> FindCompletedAsync(
            string cohortKey, Guid companyId, string familySetHash, CancellationToken ct) =>
            Task.FromResult(Seed.Concat(Written).LastOrDefault(r =>
                r.CohortKey == cohortKey
                && r.CompanyId == companyId
                && r.FamilySetHash == familySetHash
                && r.IsCompletedJudgment));
    }

    private sealed class NullBatchReader : INewsObservationBatchReader
    {
        public Task<NewsObservationBatch?> GetBatchAsync(Guid batchId, CancellationToken ct) =>
            Task.FromResult<NewsObservationBatch?>(null);
    }

    private sealed class StubAnalyzer(
        Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> respond) : INewsJudgmentAnalyzer
    {
        public List<NewsJudgmentAnalysisRequest> Requests { get; } = [];

        public Task<NewsJudgmentAnalysisOutcome> AnalyzeAsync(
            NewsJudgmentAnalysisRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static NewsJudgmentGenerator Generator(
        StubAnalyzer analyzer,
        InMemoryJudgmentStore store,
        string judgeName = "deepinfra-deepseek",
        ILogger<NewsJudgmentGenerator>? logger = null,
        Radar.Application.Filings.IReportedMetricStore? reportedMetrics = null) =>
        new(
            new NullBatchReader(),
            new NewsJudgmentReaderSet(
            [
                new NewsJudgmentReader(
                    new NewsJudgmentReaderIdentity(judgeName, "openai", "judge-model"), analyzer),
            ]),
            store,
            JudgmentOptions(),
            TimeProvider.System,
            logger ?? NullLogger<NewsJudgmentGenerator>.Instance,
            reportedMetrics);

    /// <summary>Spec 215 §2: a one-family typing result whose statement NAMES a ledger metric (backlog).</summary>
    private static NewsTypingRunResult BacklogTypingResult()
    {
        const string Statement = "Power projects lift Argan as backlog hits $2.5B";
        var factRef = NewsJudgmentTestData.FactRef(
            Eose, Guid.NewGuid(), Statement, NewsFactAssertionStatus.Reported, NewsFactAttribution.Publisher);
        var families = FactFamilyBuilder.Build(
        [
            new FactFamilyInputFact(
                FactId: factRef.Fact.FactId,
                CompanyId: Eose,
                EventTypes: factRef.Fact.EventTypes,
                Statement: Statement,
                FirstObservedAtUtc: NewsJudgmentTestData.ObservedAt,
                Publisher: "Outlet",
                ObservationId: factRef.ObservationId,
                CaptureMode: NewsObservationCaptureMode.ProspectiveRss),
        ]);

        return new NewsTypingRunResult(
            RunId: RunId,
            WindowStartUtc: NewsJudgmentTestData.ObservedAt.AddDays(-30),
            WindowEndUtc: NewsJudgmentTestData.ObservedAt.AddDays(1),
            NewsObservationBatchId: null,
            Cohorts:
            [
                new NewsTypingCohortRunResult(
                    Reader: new NewsTypingReaderIdentity(
                        "deepinfra-deepseek", "openai", "deepseek-ai/DeepSeek-V4-Flash"),
                    Families: families,
                    FactsById: new Dictionary<Guid, NewsTypingFactRef> { [factRef.Fact.FactId] = factRef },
                    TypingCompletenessByCompany: new Dictionary<Guid, NewsTypingCompleteness>
                    {
                        [Eose] = NewsTypingCompleteness.Complete,
                    },
                    FactsDroppedInWindow: 0,
                    RetryExhausted: 0),
            ]);
    }

    private sealed class FixedLedger(IReadOnlyList<Radar.Application.Filings.ReportedMetricRecord> records, bool fail = false)
        : Radar.Application.Filings.IReportedMetricStore
    {
        public Task<Radar.Application.Storage.DurableWriteResult> WriteIfNewAsync(
            Guid companyId,
            string accession,
            string policy,
            IReadOnlyList<Radar.Application.Filings.ReportedMetricRecord> records,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Radar.Application.Filings.ReportedMetricRecord>> GetForCompanyAsync(
            Guid companyId, CancellationToken ct) =>
            fail
                ? throw new IOException("ledger unreadable")
                : Task.FromResult<IReadOnlyList<Radar.Application.Filings.ReportedMetricRecord>>(
                    [.. records.Where(r => r.CompanyId == companyId)]);
    }

    /// <summary>
    /// SPEC 216 §1: a LATER accession reporting the SAME metric, which is what makes
    /// <see cref="BacklogLedgerRecord"/> eligible as a reference at all — the newest accession's figure is
    /// the CURRENT value and is never one. Filed before the family's observation instant, so the secondary
    /// guard admits the older row.
    /// </summary>
    private static Radar.Application.Filings.ReportedMetricRecord NewerBacklogLedgerRecord() =>
        BacklogLedgerRecord(Guid.Parse("1e5a0000-0000-4000-8000-00000000000c")) with
        {
            Accession = "0000100591-26-000011",
            FilingDateUtc = new DateTimeOffset(2026, 7, 9, 20, 0, 0, TimeSpan.Zero),
            Value = "2.518",
            Period = "as of April 30, 2026",
        };

    private static Radar.Application.Filings.ReportedMetricRecord BacklogLedgerRecord(Guid id) => new(
        Id: id,
        CompanyId: Eose,
        Accession: "0000100591-26-000005",
        EvidenceId: Guid.NewGuid(),
        FilingDateUtc: new DateTimeOffset(2026, 4, 9, 20, 0, 0, TimeSpan.Zero),
        Form: "8-K",
        Metric: Radar.Application.Filings.ReportedMetric.Backlog,
        Value: "2.929",
        Unit: "billion",
        Period: "as of January 31, 2026",
        PriorValue: null,
        PriorPeriod: null,
        Quote: "Project backlog of $2.929 billion as of January 31, 2026.",
        ReaderIdentity: "openai:deepseek",
        Verification: Radar.Application.Filings.ReportedMetricVerification.Verbatim,
        Policy: Radar.Application.Filings.ReportedMetricsPolicy.Version);

    [Fact]
    public async Task ALedgerReference_ReachesTheRequest_TheValidator_AndTheRecord_AndForksTheCacheKey()
    {
        // Spec 215 §2 end to end: the ledger's backlog value is projected because the supplied statement
        // names backlog; the judge cites it beside the level; the basis is ReferenceSupported; the record
        // carries the projected set and the cited set; and the family-set hash differs from the
        // reference-free one, so the verdict made with references is never reused for one without.
        var referenceId = Guid.Parse("1e5a0000-0000-4000-8000-000000000001");
        var typing = BacklogTypingResult();
        var analyzer = new StubAnalyzer(request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "Backlog of $2.5B sits below the $2.929B reported as of January 31.",
                TrajectoryFactIds: [request.Families[0].RepresentativeFactId.ToString("D")],
                TrajectoryReferenceIds: [referenceId.ToString("D")]),
            "raw-hash",
            null));
        var store = new InMemoryJudgmentStore();

        var result = await Generator(
                analyzer,
                store,
                reportedMetrics: new FixedLedger(
                    [BacklogLedgerRecord(referenceId), NewerBacklogLedgerRecord()]))
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        Assert.NotNull(result);
        var request = Assert.Single(analyzer.Requests);
        var reference = Assert.Single(request.References!);
        Assert.Equal(referenceId, reference.ReferenceId);
        Assert.Equal("2.929", reference.Value);

        var record = Assert.Single(store.Written);
        Assert.Equal(NewsJudgmentStatus.Judged, record.Status);
        Assert.Equal(NewsTrajectoryBasis.ReferenceSupported, record.TrajectoryBasis);
        Assert.Equal([referenceId], record.ReferenceIds);
        Assert.Equal(0, record.ReferenceValuesOmitted);
        Assert.Equal([referenceId], record.TrajectoryReferenceIds);
        Assert.Equal("news-judgment-v7", record.SchemaVersion);

        // Spec 216 §1/§5: the newest accession is EXCLUDED and counted, the reference policy and the
        // KINDS are persisted, and the per-family observation instant travels onto the record.
        Assert.Equal(1, record.ReferencesExcludedNewest);
        Assert.Equal(0, record.ReferencesExcludedLaterThanFact);
        Assert.Equal(0, record.ReferencesSkippedSupersededPolicy);
        Assert.Equal(Radar.Application.Filings.ReportedMetricsPolicy.Version, record.ReferencePolicy);
        Assert.Equal(
            [new NewsJudgmentReferenceRef(referenceId, NewsJudgmentReferenceKind.Prior)],
            record.ReferenceKinds);
        Assert.Equal(
            [new NewsJudgmentReferenceRef(referenceId, NewsJudgmentReferenceKind.Prior)],
            record.TrajectoryReferenceKinds);
        Assert.Equal(NewsJudgmentTestData.ObservedAt, Assert.Single(record.Families).ObservedAtUtc);

        // The same families with NO ledger hash differently — a different cache entry, never a reuse.
        var withoutLedger = new InMemoryJudgmentStore();
        await Generator(new StubAnalyzer(_ => new NewsJudgmentAnalysisOutcome(
                NewsJudgmentAnalysisFailure.ProviderError, null, null, "down")), withoutLedger)
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);
        var reference_free = Assert.Single(withoutLedger.Written);
        Assert.NotEqual(record.FamilySetHash, reference_free.FamilySetHash);
        Assert.Equal([], reference_free.ReferenceIds!);
        Assert.Equal(0, reference_free.ReferenceValuesOmitted);
    }

    [Fact]
    public async Task AnUnreadableLedger_IsOneWarning_AndTheJudgmentProceedsWithoutReferences()
    {
        var typing = BacklogTypingResult();
        var analyzer = new StubAnalyzer(request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                "Unknown", null, [], "No comparison available.", TrajectoryFactIds: []),
            "raw-hash",
            null));
        var store = new InMemoryJudgmentStore();
        var logger = new CapturingLogger<NewsJudgmentGenerator>();

        await Generator(analyzer, store, logger: logger, reportedMetrics: new FixedLedger([], fail: true))
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        var request = Assert.Single(analyzer.Requests);
        Assert.Empty(request.References!);
        var record = Assert.Single(store.Written);
        Assert.Equal([], record.ReferenceIds!);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("could not be read", StringComparison.Ordinal)
                && e.Message.Contains("NO reference values", StringComparison.Ordinal));
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("1 of 1 candidate company ledger(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoLedgerRegistered_RecordsAnEmptyProjectedSet_AndReferencesAreNull_OnTheRequest()
    {
        var typing = BacklogTypingResult();
        var analyzer = new StubAnalyzer(request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                "Unknown", null, [], "No comparison available.", TrajectoryFactIds: []),
            "raw-hash",
            null));
        var store = new InMemoryJudgmentStore();

        await Generator(analyzer, store).GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        Assert.Empty(Assert.Single(analyzer.Requests).References!);
        var record = Assert.Single(store.Written);
        Assert.Equal([], record.ReferenceIds!);
        Assert.Equal(0, record.ReferenceValuesOmitted);
        Assert.Equal([], record.TrajectoryReferenceIds!);
    }

    private static NewsJudgmentOptions JudgmentOptions() => new(
        outputDirectory: "unused",
        maxCompaniesPerRun: 30,
        maxFamiliesPerJudgment: 50,
        maxJudgmentAttempts: 3,
        presentationJudge: "deepinfra-deepseek",
        presentationExtractor: "deepinfra-deepseek",
        newsSearchCollectorName: "newssearch");

    /// <summary>Duplicated syndicated legal stories about EOSE, typed as facts, collapsed by the REAL builder.</summary>
    private static NewsTypingRunResult TypingResult(out IReadOnlyList<FactFamilyRecord> families)
    {
        const string Statement =
            "A plaintiff law firm announced an investigation into Eos Energy over securities claims.";
        var inputs = new List<FactFamilyInputFact>();
        var factsById = new Dictionary<Guid, NewsTypingFactRef>();
        for (var i = 0; i < 3; i++)
        {
            var factRef = NewsJudgmentTestData.FactRef(Eose, Guid.NewGuid(), Statement);
            factsById[factRef.Fact.FactId] = factRef;
            inputs.Add(new FactFamilyInputFact(
                FactId: factRef.Fact.FactId,
                CompanyId: Eose,
                EventTypes: factRef.Fact.EventTypes,
                Statement: Statement,
                FirstObservedAtUtc: NewsJudgmentTestData.ObservedAt.AddHours(i),
                Publisher: $"Syndicated Outlet {i}",
                ObservationId: factRef.ObservationId,
                CaptureMode: NewsObservationCaptureMode.ProspectiveRss));
        }

        families = FactFamilyBuilder.Build(inputs);

        return new NewsTypingRunResult(
            RunId: RunId,
            WindowStartUtc: NewsJudgmentTestData.ObservedAt.AddDays(-30),
            WindowEndUtc: NewsJudgmentTestData.ObservedAt.AddDays(1),
            NewsObservationBatchId: null,
            Cohorts:
            [
                new NewsTypingCohortRunResult(
                    Reader: new NewsTypingReaderIdentity(
                        "deepinfra-deepseek", "openai", "deepseek-ai/DeepSeek-V4-Flash"),
                    Families: families,
                    FactsById: factsById,
                    TypingCompletenessByCompany: new Dictionary<Guid, NewsTypingCompleteness>
                    {
                        [Eose] = NewsTypingCompleteness.Complete,
                    },
                    FactsDroppedInWindow: 2,
                    RetryExhausted: 0),
            ]);
    }

    /// <summary>
    /// Spec 187 §2: the generator now CONSUMES a frozen plan. Tests build it through the REAL
    /// <see cref="NewsJudgmentCandidatePlanner"/> at the same budget the generator is configured with, so
    /// they still exercise the production selection path rather than hand-rolling candidates.
    /// </summary>
    private static NewsJudgmentCandidatePlan Plan(IReadOnlyList<StrategyReportSection>? sections = null) =>
        new NewsJudgmentCandidatePlanner(JudgmentOptions()).Plan(sections ?? Sections());

    private static IReadOnlyList<StrategyReportSection> Sections() =>
        [
            NewsRiskTestData.Section(
                "disclosure-led-v11",
                isPrimary: true,
                StrategyPurpose.Research,
                NewsRiskTestData.Row(1, Eose, "Eos Energy", "EOSE")),
        ];

    [Fact]
    public async Task EoseEndToEnd_SyndicatedLegalStories_CollapseToOneFamily_AndTheChallengeQualifiesTheLeader()
    {
        var typing = TypingResult(out var families);
        var family = Assert.Single(families); // three syndicated copies → ONE canonical family
        Assert.Equal(3, family.MemberCount);

        var analyzer = new StubAnalyzer(request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            // Spec 187 §1 — the EOSE shape, end to end: the ONLY supplied fact is a plaintiff-firm
            // SOLICITATION, so it may carry a caveated legal challenge but cannot establish an overall
            // direction. The honest v2 read is therefore `Unknown` with NO cited trajectory evidence.
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Unknown",
                ChallengeStrength: 70,
                Findings:
                [
                    new NewsJudgmentModelFinding(
                        "RegulatoryOrLegalSetback",
                        "High",
                        0.85,
                        [request.Families[0].RepresentativeFactId.ToString("D")],
                        "Based solely on a plaintiff-firm solicitation; no filing is confirmed."),
                ],
                Rationale: "Legal scrutiny challenges the trajectory.",
                TrajectoryFactIds: []),
            "raw-hash",
            null));
        var store = new InMemoryJudgmentStore();

        var result = await Generator(analyzer, store)
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        Assert.NotNull(result);

        // The judge received ONE supplied fact for the whole syndicated story — never three.
        var request = Assert.Single(analyzer.Requests);
        var supplied = Assert.Single(request.Families);
        Assert.Equal(family.RepresentativeFactId, supplied.RepresentativeFactId);
        Assert.Equal(3, supplied.MemberCount);

        // The durable record carries the full provenance chain: run id, stage-1 cohort identity + taxonomy
        // + family-builder identity, the composed stage-2 cohort key, the family-set hash and the family
        // refs that resolve judgment → fact → observation.
        var record = Assert.Single(store.Written);
        Assert.Equal(NewsJudgmentStatus.Judged, record.Status);
        Assert.Equal(RunId, record.RunId);
        Assert.Equal(typing.Cohorts[0].Reader.CohortKey, record.Stage1CohortKey);
        Assert.Equal(NewsEventTaxonomy.TaxonomyHash, record.TaxonomyHash);
        Assert.Equal(FactFamilyBuilder.IdentityString, record.FamilyBuilderIdentity);
        Assert.Contains("stage1=" + record.Stage1CohortKey, record.CohortKey);
        var familyRef = Assert.Single(record.Families);
        Assert.Equal(family.FamilyId, familyRef.FamilyId);
        Assert.Equal(family.RepresentativeFactId, familyRef.RepresentativeFactId);
        var finding = Assert.Single(record.Findings);
        Assert.Equal(family.RepresentativeFactId, Assert.Single(finding.FactIds));

        // The marker map (presentation cohort) says CHALLENGED — EOSE cannot render as an unqualified leader.
        Assert.NotNull(result!.Markers);
        Assert.False(result.Markers!.JudgmentPending);
        var marker = result.Markers.Markers![Eose];
        Assert.Equal(NewsJudgmentMarkerState.Challenged, marker.State);
        // Spec 186 §1: every judged marker also carries the factual trajectory token.
        Assert.Equal(
            "⚠ challenged (regulatory-or-legal-setback, high) · trajectory unknown",
            marker.CellText);
        // Spec 187 §1: an Unknown read cites nothing — recorded as the EMPTY set, never as "not recorded".
        Assert.NotNull(record.TrajectoryFactIds);
        Assert.Empty(record.TrajectoryFactIds!);
        // …and the judgment id, so the report can cite the record the marker came from.
        Assert.Equal(record.JudgmentId, marker.JudgmentId);

        // The §3 error split rides the run result: stage-1 drops per cohort, stage-2 drops on the record.
        Assert.Equal(2, result.Stage1FactsDroppedByCohort[record.Stage1CohortKey]);
    }

    [Fact]
    public async Task ZeroFamilies_RecordsInsufficientFacts_WithNoModelCall()
    {
        var typing = TypingResult(out _);
        var otherCompany = Guid.NewGuid();
        var sections = new[]
        {
            NewsRiskTestData.Section(
                "disclosure-led-v11",
                isPrimary: true,
                StrategyPurpose.Research,
                NewsRiskTestData.Row(1, otherCompany, "Quiet Co", "QUIE")),
        };
        var analyzer = new StubAnalyzer(_ => throw new InvalidOperationException("must not be called"));
        var store = new InMemoryJudgmentStore();

        var result = await Generator(analyzer, store)
            .GenerateAsync(RunId, Plan(sections), typing, CancellationToken.None);

        Assert.Empty(analyzer.Requests);
        var record = Assert.Single(store.Written);
        Assert.Equal(NewsJudgmentStatus.InsufficientFacts, record.Status);
        Assert.Equal(
            "? unassessed (insufficient-facts)",
            result!.Markers!.Markers![otherCompany].CellText);
    }

    [Fact]
    public async Task CompletedJudgment_IsReusedThroughTheCache_WithThisRunsCompletenessDimensions()
    {
        var typing = TypingResult(out _);
        var analyzer = new StubAnalyzer(request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse("Unknown", null, [], "Factual read.", []),
            "raw-hash",
            null));
        var store = new InMemoryJudgmentStore();
        var generator = Generator(analyzer, store);

        var first = await generator.GenerateAsync(RunId, Plan(), typing, CancellationToken.None);
        Assert.Single(analyzer.Requests);

        // Second run over the identical family set: the SAME cohort/company/family-set hash hits the cache
        // — no second model call; the reused record carries a NEW id under the new run and cites its source.
        var secondRun = Guid.NewGuid();
        var second = await generator.GenerateAsync(secondRun, Plan(), typing, CancellationToken.None);

        Assert.Single(analyzer.Requests); // still one call
        Assert.Equal(2, store.Written.Count);
        var reused = store.Written[1];
        Assert.Equal(store.Written[0].JudgmentId, reused.ReusedFromJudgmentId);
        Assert.Equal(secondRun, reused.RunId);
        Assert.Equal(NewsJudgmentStatus.Judged, reused.Status);
        // The reused verdict still derives this run's marker (same-run record ⇒ not stale).
        Assert.Equal(
            NewsJudgmentMarkerState.NoChallengeFound, second!.Markers!.Markers![Eose].State);
        Assert.NotNull(first);
    }

    /// <summary>
    /// Spec 189 §2: a completed verdict stays reusable across the widened completeness vocabulary, and the
    /// reused record carries the CURRENT run's token — never the one the original call was made under. A
    /// cached verdict replayed while this run's typing degraded must SAY the read degraded.
    /// </summary>
    [Fact]
    public async Task AReusedVerdict_CarriesTheCurrentRunsCompletenessToken_NotTheCachedOne()
    {
        var typing = TypingResult(out _);
        var analyzer = new StubAnalyzer(request => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse("Unknown", null, [], "Factual read.", []),
            "raw-hash",
            null));
        var store = new InMemoryJudgmentStore();
        var generator = Generator(analyzer, store);

        await generator.GenerateAsync(RunId, Plan(), typing, CancellationToken.None);
        Assert.Equal(NewsTypingCompleteness.Complete, store.Written[0].TypingCompleteness);

        // The SAME family set (so the cache hits) under a run whose typing pass degraded for this company.
        var degraded = typing with
        {
            Cohorts =
            [
                typing.Cohorts[0] with
                {
                    TypingCompletenessByCompany = new Dictionary<Guid, NewsTypingCompleteness>
                    {
                        [Eose] = NewsTypingCompleteness.RetryableFailure,
                    },
                },
            ],
        };

        await generator.GenerateAsync(Guid.NewGuid(), Plan(), degraded, CancellationToken.None);

        Assert.Single(analyzer.Requests); // still ONE model call: the verdict was reused …
        var reused = store.Written[1];
        Assert.Equal(store.Written[0].JudgmentId, reused.ReusedFromJudgmentId);
        Assert.Equal(NewsJudgmentStatus.Judged, reused.Status);
        // … while EVERY completeness dimension is this run's.
        Assert.Equal(NewsTypingCompleteness.RetryableFailure, reused.TypingCompleteness);
        Assert.Equal("news-judgment-v7", reused.SchemaVersion);
    }

    [Fact]
    public async Task ProviderFailure_IsRecorded_AndNeverCached()
    {
        var typing = TypingResult(out _);
        var calls = 0;
        var analyzer = new StubAnalyzer(_ =>
        {
            calls++;
            return new NewsJudgmentAnalysisOutcome(
                NewsJudgmentAnalysisFailure.ProviderError, null, null, "HttpRequestException: down");
        });
        var store = new InMemoryJudgmentStore();
        var generator = Generator(analyzer, store);

        var result = await generator.GenerateAsync(RunId, Plan(), typing, CancellationToken.None);
        Assert.Equal(NewsJudgmentStatus.ProviderFailure, Assert.Single(store.Written).Status);
        Assert.Equal("? unassessed (provider-failure)", result!.Markers!.Markers![Eose].CellText);

        // A failure is persisted but never reused — the retry issues a fresh model call.
        await generator.GenerateAsync(Guid.NewGuid(), Plan(), typing, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NullTyping_ReturnsNull_TheJudgeCannotRunWithoutStageOne()
    {
        var analyzer = new StubAnalyzer(_ => throw new InvalidOperationException("must not be called"));
        var store = new InMemoryJudgmentStore();

        var result = await Generator(analyzer, store)
            .GenerateAsync(RunId, Plan(), typing: null, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(store.Written);
        Assert.Empty(analyzer.Requests);
    }

    [Fact]
    public async Task UnresolvablePresentationCohort_ReturnsNullMarkers_NeverAnUndesignatedSource()
    {
        var typing = TypingResult(out _);
        var analyzer = new StubAnalyzer(_ => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse("Unknown", null, [], "Factual read.", []),
            "h",
            null));
        var store = new InMemoryJudgmentStore();

        // The configured presentation judge name matches no judge this run.
        var result = await Generator(analyzer, store, judgeName: "some-other-judge")
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.Markers);
        Assert.Single(store.Written); // judgments still persist — only the marker source is withheld
    }

    // ── Spec 192 §2: the rationale-length facts, and the aggregated per-cohort signal ─────────────

    /// <summary>Ordinary prose of an exact length — no advice language, no trailing space to be trimmed.</summary>
    private static string Rationale(int length)
    {
        const string Prose =
            "The single supplied family is a plaintiff-firm solicitation, which is reported but not "
            + "confirmed, so it qualifies rather than establishes the direction. ";
        var text = new System.Text.StringBuilder();
        while (text.Length < length)
        {
            text.Append(Prose);
        }

        var exact = text.ToString(0, length);
        return char.IsWhiteSpace(exact[^1]) ? string.Concat(exact.AsSpan(0, length - 1), ".") : exact;
    }

    private static NewsJudgmentAnalysisOutcome Judged(string rationale) => new(
        NewsJudgmentAnalysisFailure.None,
        new NewsJudgmentModelResponse("Unknown", null, [], rationale, []),
        "raw-hash",
        null);

    private static IReadOnlyList<string> OverSoftLimitLines(
        CapturingLogger<NewsJudgmentGenerator> logger) =>
        [.. logger.Entries
            .Where(e => e.Message.Contains("soft bound", StringComparison.Ordinal))
            .Select(e => e.Message)];

    [Fact]
    public async Task AnOverLongRationale_IsStillJudged_AndTheRecordCarriesTheMeasuredFacts()
    {
        // Spec 192: the pre-192 validator would have failed this response outright and nulled the text.
        var typing = TypingResult(out _);
        var rationale = Rationale(1_228);
        var analyzer = new StubAnalyzer(_ => Judged(rationale));
        var store = new InMemoryJudgmentStore();
        var logger = new CapturingLogger<NewsJudgmentGenerator>();

        await Generator(analyzer, store, logger: logger)
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        var record = Assert.Single(store.Written);
        Assert.Equal(NewsJudgmentStatus.Judged, record.Status);
        Assert.Equal(rationale, record.Rationale); // persisted IN FULL, never truncated
        Assert.Equal(1_228, record.RationaleLength);
        Assert.True(record.RationaleOverSoftLimit);

        // ONE aggregated Information line for the cohort (the spec-145 precedent), not one per judgment.
        var line = Assert.Single(OverSoftLimitLines(logger));
        Assert.Contains("1 judgment(s)", line, StringComparison.Ordinal);
        Assert.Contains(record.CohortKey, line, StringComparison.Ordinal);
        Assert.Equal(
            LogLevel.Information,
            Assert.Single(logger.Entries, e => e.Message.Contains("soft bound", StringComparison.Ordinal))
                .Level);
    }

    [Fact]
    public async Task AWithinBoundRationale_RecordsTheLengthAndNoFlag_AndSaysNothingInTheSummary()
    {
        var typing = TypingResult(out _);
        var rationale = Rationale(120);
        var analyzer = new StubAnalyzer(_ => Judged(rationale));
        var store = new InMemoryJudgmentStore();
        var logger = new CapturingLogger<NewsJudgmentGenerator>();

        await Generator(analyzer, store, logger: logger)
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        var record = Assert.Single(store.Written);
        Assert.Equal(120, record.RationaleLength);
        Assert.False(record.RationaleOverSoftLimit); // recorded false, which is NOT "not recorded"
        Assert.Empty(OverSoftLimitLines(logger));
    }

    /// <summary>
    /// Spec 192 §2 with spec 188 §1: a REUSED verdict carries the cached rationale facts (otherwise a
    /// replayed judgment would read as "not recorded" beside the very rationale it carries forward) — but
    /// it is NOT counted as this pass's activity, because no provider call was made.
    /// </summary>
    [Fact]
    public async Task AReusedVerdict_CarriesTheCachedRationaleFacts_ButIsNotCountedAsThisPassesActivity()
    {
        var typing = TypingResult(out _);
        var rationale = Rationale(1_095);
        var analyzer = new StubAnalyzer(_ => Judged(rationale));
        var store = new InMemoryJudgmentStore();

        var firstLogger = new CapturingLogger<NewsJudgmentGenerator>();
        await Generator(analyzer, store, logger: firstLogger)
            .GenerateAsync(RunId, Plan(), typing, CancellationToken.None);
        Assert.Single(OverSoftLimitLines(firstLogger));

        var secondLogger = new CapturingLogger<NewsJudgmentGenerator>();
        await Generator(analyzer, store, logger: secondLogger)
            .GenerateAsync(Guid.NewGuid(), Plan(), typing, CancellationToken.None);

        Assert.Single(analyzer.Requests); // served from the completed-judgment cache
        var reused = store.Written[1];
        Assert.NotNull(reused.ReusedFromJudgmentId);
        Assert.Equal(1_095, reused.RationaleLength);
        Assert.True(reused.RationaleOverSoftLimit);
        // …and the second pass, which called nothing, reports nothing.
        Assert.Empty(OverSoftLimitLines(secondLogger));
    }

    [Fact]
    public async Task AProviderFailure_LeavesTheRationaleFactsNotRecorded()
    {
        // No validated response existed, so there was nothing to measure: `null` is the honest value, and
        // it is the same "not recorded" a pre-192 record on disk hydrates with.
        var typing = TypingResult(out _);
        var analyzer = new StubAnalyzer(_ => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.ProviderError, null, null, "HttpRequestException: down"));
        var store = new InMemoryJudgmentStore();

        await Generator(analyzer, store).GenerateAsync(RunId, Plan(), typing, CancellationToken.None);

        var record = Assert.Single(store.Written);
        Assert.Equal(NewsJudgmentStatus.ProviderFailure, record.Status);
        Assert.Null(record.RationaleLength);
        Assert.Null(record.RationaleOverSoftLimit);
    }
}
