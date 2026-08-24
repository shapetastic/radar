using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Tests.Ai;
using Radar.Application.Tests.NewsRisk;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 181 §4/§6: bounded per-reader selection (window newest-first, then backlog oldest-first), the
/// completed-typing cache (nothing typed twice; failures retried), one extractor call per observation,
/// per-cohort fact-family checkpoints, the decomposition artifact, and the never-abort failure posture.
/// </summary>
public sealed class NewsTypingGeneratorTests
{
    private static readonly DateTimeOffset AsOf = NewsTypingTestData.AsOf;
    private static readonly Guid RunId = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid CompanyId = new("aaaaaaaa-0000-0000-0000-000000000001");

    // The clock is the SHARED Radar.Application.Tests.Ai.MutableTimeProvider: spec 186 §2's retry lane is
    // FIFO by last-attempt instant (so runs must be distinguishable) and spec 187 §7 additionally needs a
    // controllable MONOTONIC timestamp. One fake serves both; a second copy would drift.

    // ---------------------------------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------------------------------

    private sealed class FakeRunStore : IPipelineRunStore
    {
        public List<PipelineRunRecord> Records { get; } = [];

        public Task<string> WriteAsync(PipelineRunRecord record, CancellationToken ct) =>
            Task.FromResult("(unused)");

        public Task<IReadOnlyList<PipelineRunRecord>> ReadRecentAsync(int count, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PipelineRunRecord>>(Records.Take(count).ToList());

        public Task<IReadOnlyList<PipelineRunRecord>> ReadBetweenAsync(
            DateTimeOffset startInclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PipelineRunRecord>>([]);
    }

    private sealed class FakeArchive : INewsObservationArchive, INewsObservationBatchReader
    {
        public List<NewsObservationRecord> Observations { get; } = [];

        public NewsObservationBatch? Batch { get; set; }

        public Task<NewsObservationWriteOutcome> WriteAsync(
            NewsObservationRecord record, CancellationToken ct) =>
            Task.FromResult(NewsObservationWriteOutcome.Written);

        public Task<bool> WriteBatchAsync(NewsObservationBatch batch, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<NewsObservationRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NewsObservationRecord>>(Observations);

        public Task<NewsObservationBatch?> GetBatchAsync(Guid batchId, CancellationToken ct) =>
            Task.FromResult(Batch?.BatchId == batchId ? Batch : null);
    }

    private sealed class InMemoryTypingStore : INewsTypingStore
    {
        public List<NewsTypingRecord> Records { get; } = [];

        /// <summary>Spec 187 §3: a durable outcome write that FAILS (the store's documented false path).</summary>
        public bool FailWrites { get; set; }

        /// <summary>Spec 187 §3: a crash seam AFTER the reservation and BEFORE the outcome is persisted.</summary>
        public bool ThrowOnWrite { get; set; }

        public Task<bool> WriteAsync(NewsTypingRecord record, CancellationToken ct)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("outcome store crashed");
            }

            if (FailWrites)
            {
                return Task.FromResult(false);
            }

            if (Records.All(r => r.TypingId != record.TypingId))
            {
                Records.Add(record);
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<NewsTypingRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NewsTypingRecord>>(
                Records.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.TypingId).ToList());

        public Task<NewsTypingRecord?> FindCompletedAsync(
            string cohortKey, Guid observationId, string payloadHash, CancellationToken ct) =>
            Task.FromResult(Records
                .Where(r => r.CohortKey == cohortKey
                    && r.ObservationId == observationId
                    && r.PayloadHash == payloadHash
                    && r.IsCompletedTyping)
                .OrderByDescending(r => r.CreatedAtUtc)
                .ThenBy(r => r.TypingId)
                .FirstOrDefault());
    }

    /// <summary>
    /// An in-memory stand-in for the spec-187 §3 durable pre-call attempt ledger. Reproduces the ONE rule
    /// that matters: a reservation id may be claimed exactly once, and a refusal returns <c>false</c>
    /// (never an exception). <see cref="PreClaim"/> simulates another process having already won an
    /// ordinal; <see cref="RefuseAll"/> simulates a ledger that cannot record anything.
    /// </summary>
    private sealed class InMemoryAttemptLedger : INewsTypingAttemptLedger
    {
        public List<NewsTypingAttemptReservation> Reservations { get; } = [];

        public bool RefuseAll { get; set; }

        /// <summary>Ordinals a "concurrent process" wins first, so THIS caller is refused them.</summary>
        public HashSet<int> RefuseOrdinals { get; } = [];

        /// <summary>Every <see cref="TryReserveAsync"/> call, refused ones included — the proof that a refusal skips rather than escalating to the next ordinal.</summary>
        public List<NewsTypingAttemptReservation> Attempted { get; } = [];

        public Task<IReadOnlyList<NewsTypingAttemptReservation>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NewsTypingAttemptReservation>>(Reservations
                .OrderBy(r => r.ReservedAtUtc)
                .ThenBy(r => r.ReservationId)
                .ToList());

        public Task<bool> TryReserveAsync(NewsTypingAttemptReservation reservation, CancellationToken ct)
        {
            Attempted.Add(reservation);
            if (RefuseAll
                || RefuseOrdinals.Contains(reservation.AttemptOrdinal)
                || Reservations.Any(r => r.ReservationId == reservation.ReservationId))
            {
                return Task.FromResult(false);
            }

            Reservations.Add(reservation);
            return Task.FromResult(true);
        }

        /// <summary>Claims an ordinal on behalf of a "concurrent process", without any outcome record.</summary>
        public void PreClaim(
            string cohortKey,
            Guid observationId,
            string payloadHash,
            int ordinal,
            DateTimeOffset reservedAtUtc) =>
            Reservations.Add(NewsTypingAttemptReservation.For(
                cohortKey, observationId, payloadHash, ordinal, null, "openai", "test-model-0",
                reservedAtUtc));
    }

    private sealed class InMemoryFamilyStore : IFactFamilySnapshotStore
    {
        public List<(string PolicySegment, FactFamilySnapshot Snapshot)> Snapshots { get; } = [];

        public Task<bool> WriteAsync(string policySegment, FactFamilySnapshot snapshot, CancellationToken ct)
        {
            Snapshots.Add((policySegment, snapshot));
            return Task.FromResult(true);
        }
    }

    private sealed class InMemoryArtifactStore : INewsTypingArtifactStore
    {
        public List<(string DateToken, string Markdown, NewsTypingDecompositionDocument Document)> Live { get; } = [];

        public List<(string DateToken, string Reason)> Failed { get; } = [];

        public Task WriteDecompositionAsync(
            string asOfDateToken,
            string markdown,
            NewsTypingDecompositionDocument document,
            CancellationToken ct)
        {
            Live.Add((asOfDateToken, markdown, document));
            return Task.CompletedTask;
        }

        public Task WriteFailedAsync(string asOfDateToken, string reason, CancellationToken ct)
        {
            Failed.Add((asOfDateToken, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedExtractor : INewsTypingExtractor
    {
        public List<Guid> ObservationsSeen { get; } = [];

        /// <summary>
        /// Spec 187 §7: the clock this fake advances to SIMULATE call latency, and the scripted duration of
        /// the n-th call (1-based). Advancing from inside the fake is what lets the generator's monotonic
        /// bracket measure an exact duration with no wall-clock sleep.
        /// </summary>
        public MutableTimeProvider? Clock { get; set; }

        public Func<int, TimeSpan>? CallDuration { get; set; }

        public Func<NewsTypingExtractionRequest, NewsTypingExtractionOutcome> Script { get; set; } =
            request => new NewsTypingExtractionOutcome(
                NewsTypingExtractionFailure.None,
                new NewsTypingModelResponse(
                    "CompanySpecific",
                    [
                        new NewsTypingModelFact(
                            EventTypes: ["RegulatoryOrLegal"],
                            Statement: request.Observation.Headline,
                            TemporalScope: null,
                            Attribution: "publisher",
                            AssertionStatus: "reported",
                            Confidence: 0.8,
                            Citations: [request.Observation.Headline]),
                    ]),
                RawResponseHash: "raw-hash",
                FailureDetail: null);

        public Task<NewsTypingExtractionOutcome> ExtractAsync(
            NewsTypingExtractionRequest request, CancellationToken ct)
        {
            ObservationsSeen.Add(request.Observation.ObservationId);
            if (Clock is not null && CallDuration is not null)
            {
                Clock.AdvanceTimestamp(CallDuration(ObservationsSeen.Count));
            }

            return Task.FromResult(Script(request));
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private sealed class Harness
    {
        public FakeRunStore RunStore { get; } = new();

        public FakeArchive Archive { get; } = new();

        public InMemoryTypingStore Store { get; } = new();

        public InMemoryAttemptLedger Ledger { get; } = new();

        public InMemoryFamilyStore FamilyStore { get; } = new();

        public InMemoryArtifactStore ArtifactStore { get; } = new();

        public ScriptedExtractor Extractor { get; } = new();

        public MutableTimeProvider Time { get; } = new(NewsTypingTestData.AsOf.AddMinutes(10));

        /// <summary>Spec 187 §7: set to capture the pass's log lines instead of discarding them.</summary>
        public CapturingLogger<NewsTypingGenerator>? Logger { get; set; }

        public NewsTypingGenerator Build(
            int maxNewTypingsPerRun = 200,
            int readers = 1,
            int maxTypingAttempts = 3,
            int maxRetryTypingsPerRun = 25,
            int maxCandidateTypingsPerRun = 100)
        {
            var readerList = new List<NewsTypingReader>();
            for (var i = 0; i < readers; i++)
            {
                readerList.Add(new NewsTypingReader(
                    new NewsTypingReaderIdentity($"reader-{i}", "openai", $"test-model-{i}"),
                    Extractor));
            }

            return new NewsTypingGenerator(
                RunStore,
                Archive,
                Archive,
                new NewsTypingReaderSet(readerList),
                Store,
                Ledger,
                FamilyStore,
                ArtifactStore,
                new NewsTypingOptions(
                    "data/news-typing",
                    maxNewTypingsPerRun,
                    lookbackDays: 30,
                    maxTypingAttempts: maxTypingAttempts,
                    maxRetryTypingsPerRun: maxRetryTypingsPerRun,
                    maxCandidateTypingsPerRun: maxCandidateTypingsPerRun),
                Time,
                (ILogger<NewsTypingGenerator>?)Logger ?? NullLogger<NewsTypingGenerator>.Instance);
        }
    }

    private static NewsObservationRecord Observation(
        string headline,
        DateTimeOffset observedAtUtc,
        Guid? companyId = null,
        string publisher = "Example Wire",
        NewsObservationCaptureMode captureMode = NewsObservationCaptureMode.ProspectiveRss)
    {
        var id = Guid.NewGuid();
        return new NewsObservationRecord(
            SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
            ObservationId: id,
            CompanyId: companyId ?? CompanyId,
            Ticker: "TST",
            Collector: "newssearch",
            QueryPhrase: "Test Co",
            FeedId: null,
            FeedName: "newssearch: Test Co",
            GoogleLandingUrl: "https://news.google.com/articles/" + id.ToString("N"),
            Publisher: publisher,
            PublisherSiteUrl: null,
            Headline: headline,
            DescriptionRaw: null,
            DescriptionText: null,
            DescriptionTruncated: false,
            PublishedAtUtc: null,
            RetrievedAtUtc: observedAtUtc,
            FirstObservedAtUtc: observedAtUtc,
            PayloadHash: "hash-" + id.ToString("N"),
            CaptureMode: captureMode,
            ArticleFetch: null);
    }

    private static PipelineRunRecord RunRecord(Guid? batchId = null) => new(
        Id: RunId,
        CreatedAtUtc: AsOf,
        Collectors: ["newssearch"],
        EvidenceCollected: 0,
        EvidenceNew: 0,
        SignalsExtracted: 0,
        SignalsValid: 0,
        SignalsApproved: 0,
        SignalsNeedingReview: 0,
        CompaniesScored: 0,
        SourcesChecked: 0,
        SourcesFailed: 0,
        ReportId: null,
        NewsObservationBatchId: batchId);

    // ---------------------------------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TypesEachObservation_OneModelCallEach_AndPersistsEveryAttempt()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Archive.Observations.Add(Observation("Company wins large contract", AsOf.AddDays(-1)));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        Assert.Equal(2, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(2, harness.Store.Records.Count);
        Assert.All(harness.Store.Records, r =>
        {
            Assert.Equal(NewsTypingStatus.Typed, r.Status);
            Assert.Equal(RunId, r.RunId);
            Assert.Equal(NewsEventTaxonomy.TaxonomyHash, r.TaxonomyHash);
            Assert.Single(r.Facts);
        });
    }

    [Fact]
    public async Task PerReaderCap_BoundsNewTypings_WindowNewestFirst_ThenBacklogOldestFirst()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var backlogOld = Observation("backlog older", AsOf.AddDays(-90));
        var backlogNewer = Observation("backlog newer", AsOf.AddDays(-60));
        var windowOlder = Observation("window older", AsOf.AddDays(-5));
        var windowNewest = Observation("window newest", AsOf.AddDays(-1));
        harness.Archive.Observations.AddRange([backlogOld, backlogNewer, windowOlder, windowNewest]);

        await harness.Build(maxNewTypingsPerRun: 3).GenerateAsync(RunId, CancellationToken.None);

        // Window first (newest first), then backlog (oldest first); the cap cuts the rest.
        Assert.Equal(
            [windowNewest.ObservationId, windowOlder.ObservationId, backlogOld.ObservationId],
            harness.Extractor.ObservationsSeen);
    }

    [Fact]
    public async Task CompletedTypings_AreNeverRetyped_ButFailuresAreRetried()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var observation = Observation("Company faces legal scrutiny", AsOf.AddDays(-2));
        harness.Archive.Observations.Add(observation);

        // First pass completes the typing.
        await harness.Build().GenerateAsync(RunId, CancellationToken.None);
        Assert.Single(harness.Store.Records);

        // Second pass (a different run): the completed cache skips it — no new model call.
        harness.Extractor.ObservationsSeen.Clear();
        var secondRunId = Guid.NewGuid();
        harness.RunStore.Records.Insert(0, RunRecord() with { Id = secondRunId });
        await harness.Build().GenerateAsync(secondRunId, CancellationToken.None);
        Assert.Empty(harness.Extractor.ObservationsSeen);
        Assert.Single(harness.Store.Records);

        // A FAILED attempt, by contrast, is retried by a later run under a NEW run-scoped id.
        var failing = Observation("provider will fail here", AsOf.AddDays(-1));
        harness.Archive.Observations.Add(failing);
        harness.Extractor.Script = request =>
            request.Observation.ObservationId == failing.ObservationId
                ? new NewsTypingExtractionOutcome(
                    NewsTypingExtractionFailure.ProviderError, null, null, "boom")
                : throw new InvalidOperationException("only the failing observation should be re-read");
        harness.Extractor.ObservationsSeen.Clear();
        var thirdRunId = Guid.NewGuid();
        harness.RunStore.Records.Insert(0, RunRecord() with { Id = thirdRunId });
        await harness.Build().GenerateAsync(thirdRunId, CancellationToken.None);
        Assert.Equal([failing.ObservationId], harness.Extractor.ObservationsSeen);
        Assert.Equal(
            NewsTypingStatus.ProviderFailure,
            harness.Store.Records.Single(r => r.ObservationId == failing.ObservationId).Status);

        harness.Extractor.ObservationsSeen.Clear();
        var fourthRunId = Guid.NewGuid();
        harness.RunStore.Records.Insert(0, RunRecord() with { Id = fourthRunId });
        await harness.Build().GenerateAsync(fourthRunId, CancellationToken.None);
        Assert.Equal([failing.ObservationId], harness.Extractor.ObservationsSeen);
        Assert.Equal(
            2,
            harness.Store.Records.Count(r => r.ObservationId == failing.ObservationId));
    }

    [Fact]
    public async Task EachReaderTypesIndependently_UnderItsOwnCohortKey()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));

        await harness.Build(readers: 2).GenerateAsync(RunId, CancellationToken.None);

        Assert.Equal(2, harness.Store.Records.Count);
        Assert.Equal(2, harness.Store.Records.Select(r => r.CohortKey).Distinct().Count());
        // One family checkpoint per cohort — never pooled.
        Assert.Equal(2, harness.FamilyStore.Snapshots.Count);
        Assert.Equal(
            2, harness.FamilyStore.Snapshots.Select(s => s.Snapshot.CohortKey).Distinct().Count());
    }

    [Fact]
    public async Task FamilyCheckpoint_CoversAllCompletedWindowTypings_NotOnlyThisRuns()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var first = Observation("Company faces legal scrutiny after complaint filed", AsOf.AddDays(-3));
        harness.Archive.Observations.Add(first);
        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        // A later run types only the NEW syndicated copy — but the checkpoint must still collapse both
        // facts into ONE family, because it runs over ALL completed typings in the window.
        var second = Observation(
            "Company faces legal scrutiny after a complaint filed", AsOf.AddDays(-2), publisher: "Other");
        harness.Archive.Observations.Add(second);
        harness.Extractor.ObservationsSeen.Clear();
        var secondRunId = Guid.NewGuid();
        harness.RunStore.Records.Insert(0, RunRecord() with { Id = secondRunId });
        await harness.Build().GenerateAsync(secondRunId, CancellationToken.None);

        Assert.Equal([second.ObservationId], harness.Extractor.ObservationsSeen);
        var checkpoint = harness.FamilyStore.Snapshots[^1].Snapshot;
        var family = Assert.Single(checkpoint.Families);
        Assert.Equal(2, family.MemberCount);
        Assert.Equal(2, family.DistinctPublisherCount);
        Assert.Equal(FactFamilyBuilder.IdentityString, checkpoint.BuilderIdentity);
    }

    [Fact]
    public async Task FamilyCheckpoint_SegmentsOverTheFullHistory_ButProjectsOnlyTheWindow()
    {
        // Spec 186 section 4: stage 1 sees ALL qualifying validated facts (the out-of-window anchor
        // included), so the episode's durable id is anchored on it; stage 2 projects the WINDOW alone, so
        // the snapshot's representative/counters stay window-only and the window counters keep their
        // spec-181 basis.
        const string Anchor = "Company faces legal scrutiny after investor complaint filed";
        const string Fresh = "Company faces legal scrutiny after an investor complaint filed";
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation(Anchor, AsOf.AddDays(-33)));
        harness.Archive.Observations.Add(
            Observation(Fresh, AsOf.AddDays(-28), publisher: "Second Outlet"));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var checkpoint = Assert.Single(harness.FamilyStore.Snapshots).Snapshot;
        var family = Assert.Single(checkpoint.Families);
        Assert.Equal(1, family.MemberCount); // window projection: the aged-out anchor is not a member
        Assert.Equal(Fresh, family.RepresentativeStatement);
        Assert.Equal(1, checkpoint.FactsConsidered); // WINDOW basis, unchanged from spec 181
        Assert.Equal(0, checkpoint.FactsWithoutCompany);

        // The id is anchored on the OUT-OF-WINDOW first-ever member's date + event types + claim key —
        // proof that stage 1 read the whole history rather than the window.
        Assert.Equal(
            FactFamilyBuilder.FamilyIdFor(
                CompanyId,
                NewsObservationCaptureMode.ProspectiveRss,
                DateOnly.FromDateTime(AsOf.AddDays(-33).UtcDateTime),
                [NewsEventType.RegulatoryOrLegal],
                FactFamilyBuilder.NormalizeStatement(Anchor)),
            family.FamilyId);

        // Control: without the out-of-window fact the SAME window would anchor on the fresh fact — so the
        // assertion above cannot pass vacuously.
        var control = new Harness();
        control.RunStore.Records.Add(RunRecord());
        control.Archive.Observations.Add(
            Observation(Fresh, AsOf.AddDays(-28), publisher: "Second Outlet"));
        await control.Build().GenerateAsync(RunId, CancellationToken.None);
        Assert.NotEqual(
            family.FamilyId,
            Assert.Single(Assert.Single(control.FamilyStore.Snapshots).Snapshot.Families).FamilyId);
    }

    [Fact]
    public async Task Decomposition_RendersFamilyCountBesideRawCount_AndBacklogMarksIncomplete()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Archive.Observations.Add(Observation("Company faces more legal scrutiny", AsOf.AddDays(-1)));

        // Cap 1: one observation stays untyped, so the company must be marked incomplete.
        await harness.Build(maxNewTypingsPerRun: 1).GenerateAsync(RunId, CancellationToken.None);

        var (_, markdown, document) = Assert.Single(harness.ArtifactStore.Live);
        Assert.Contains(NewsTypingDecompositionDocument.Caveat181, markdown);
        var company = Assert.Single(document.Companies);
        Assert.Equal(2, company.ObservationsInWindow);
        Assert.True(company.Incomplete);
        Assert.Contains(
            company.IncompleteReasons, r => r.Contains("typing backlog", StringComparison.Ordinal));
        var cohort = Assert.Single(company.Cohorts);
        Assert.Equal(1, cohort.ObservationsTyped);
        Assert.Equal(1, cohort.UntypedRemaining);
        var row = Assert.Single(cohort.Types);
        Assert.Equal(NewsEventType.RegulatoryOrLegal, row.EventType);
        Assert.Equal(1, row.ObservationCount);
        Assert.Equal(1, row.FamilyCount);
    }

    [Fact]
    public async Task TypeRowFamilyCount_CountsOnlyFamiliesAnchoredInThatRowsObservations()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var legal = Observation("Company faces legal scrutiny", AsOf.AddDays(-2));
        var financing = Observation("Company announces dilutive offering", AsOf.AddDays(-1));
        harness.Archive.Observations.AddRange([legal, financing]);

        // The financing observation carries a secondary legal fact, so its primary type is
        // FinancingOrDilution while a RegulatoryOrLegal family still exists for it.
        harness.Extractor.Script = request =>
        {
            var facts = request.Observation.ObservationId == financing.ObservationId
                ? new[]
                {
                    Fact(request, "FinancingOrDilution", confidence: 0.9),
                    Fact(request, "RegulatoryOrLegal", confidence: 0.3),
                }
                : [Fact(request, "RegulatoryOrLegal", confidence: 0.8)];
            return new NewsTypingExtractionOutcome(
                NewsTypingExtractionFailure.None,
                new NewsTypingModelResponse("CompanySpecific", facts),
                RawResponseHash: "raw-hash",
                FailureDetail: null);
        };

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var (_, _, document) = Assert.Single(harness.ArtifactStore.Live);
        var cohort = Assert.Single(Assert.Single(document.Companies).Cohorts);
        Assert.Equal(3, cohort.FamilyCount);

        // The financing observation's legal-fact family is NOT anchored in any observation whose
        // primary type is RegulatoryOrLegal, so the legal row must not count it.
        var legalRow = cohort.Types.Single(r => r.EventType == NewsEventType.RegulatoryOrLegal);
        Assert.Equal(1, legalRow.ObservationCount);
        Assert.Equal(1, legalRow.FamilyCount);
        var financingRow = cohort.Types.Single(r => r.EventType == NewsEventType.FinancingOrDilution);
        Assert.Equal(1, financingRow.ObservationCount);
        Assert.Equal(1, financingRow.FamilyCount);
    }

    private static NewsTypingModelFact Fact(
        NewsTypingExtractionRequest request, string eventType, double confidence) => new(
        EventTypes: [eventType],
        Statement: request.Observation.Headline,
        TemporalScope: null,
        Attribution: "publisher",
        AssertionStatus: "reported",
        Confidence: confidence,
        Citations: [request.Observation.Headline]);

    [Fact]
    public async Task ProvenFullUniverseBatch_ClearsTheCaptureCaveat()
    {
        var harness = new Harness();
        var batchId = Guid.NewGuid();
        harness.RunStore.Records.Add(RunRecord(batchId));
        harness.Archive.Batch = new NewsObservationBatch(
            BatchId: batchId,
            RunAsOfUtc: AsOf,
            SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
            FullUniverse: true,
            ObservationsAttempted: 1,
            ObservationsWritten: 1,
            ObservationsCrossRunDeduped: 0,
            ObservationsFailed: 0,
            CaptureProven: true,
            Collectors: []);
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var (_, _, document) = Assert.Single(harness.ArtifactStore.Live);
        Assert.True(document.CaptureProvenThisRun);
        Assert.False(Assert.Single(document.Companies).Incomplete);
    }

    [Fact]
    public async Task NoResolvableBatch_ReadsAsUnprovenCapture_AndMarksCompaniesIncomplete()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var (_, _, document) = Assert.Single(harness.ArtifactStore.Live);
        Assert.Null(document.CaptureProvenThisRun);
        Assert.True(Assert.Single(document.Companies).Incomplete);
    }

    [Fact]
    public async Task CaptureModes_SplitIntoSeparateCohortRows_NeverPooled()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Prospective story about scrutiny", AsOf.AddDays(-2)));
        harness.Archive.Observations.Add(Observation(
            "Legacy story about scrutiny", AsOf.AddDays(-1),
            captureMode: NewsObservationCaptureMode.LegacyHeadlineOnly));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var (_, _, document) = Assert.Single(harness.ArtifactStore.Live);
        var company = Assert.Single(document.Companies);
        Assert.Equal(2, company.Cohorts.Count);
        Assert.Equal(
            [NewsObservationCaptureMode.ProspectiveRss, NewsObservationCaptureMode.LegacyHeadlineOnly],
            company.Cohorts.Select(c => c.CaptureMode));
        Assert.All(company.Cohorts, c => Assert.Equal(1, c.ObservationsTyped));
    }

    [Fact]
    public async Task GeneratorFailure_WritesTheNamedFailedArtifact_AndNeverThrows()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => throw new InvalidOperationException("catastrophic");

        // The scripted throw is caught per observation as a provider failure — force a DEEPER failure by
        // making the artifact write itself throw is out of scope; instead break the archive read.
        var broken = new ThrowingArchive();
        var generator = new NewsTypingGenerator(
            harness.RunStore,
            broken,
            harness.Archive,
            new NewsTypingReaderSet(
                [new NewsTypingReader(new NewsTypingReaderIdentity("r", "openai", "m"), harness.Extractor)]),
            harness.Store,
            harness.Ledger,
            harness.FamilyStore,
            harness.ArtifactStore,
            new NewsTypingOptions("data/news-typing", 10, 30, 3, 5, 4),
            harness.Time,
            NullLogger<NewsTypingGenerator>.Instance);

        await generator.GenerateAsync(RunId, CancellationToken.None);

        var (_, reason) = Assert.Single(harness.ArtifactStore.Failed);
        Assert.Contains("InvalidOperationException", reason);
        Assert.Empty(harness.ArtifactStore.Live);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 186 §2 — bounded, FIFO-fair typing retries
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Registers and returns a fresh run id, so each simulated pass is a genuinely NEW run.</summary>
    private static Guid NextRun(Harness harness)
    {
        var id = Guid.NewGuid();
        harness.RunStore.Records.Insert(0, RunRecord() with { Id = id });
        return id;
    }

    private static NewsTypingExtractionOutcome ProviderFailure() =>
        new(NewsTypingExtractionFailure.ProviderError, null, null, "boom");

    [Fact]
    public async Task PersistentlyFailingObservation_IsAttemptedExactlyMaxAttempts_ThenLeavesSelection()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("provider always fails", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => ProviderFailure();

        // Spec 187 §4: exhaustion is IMMEDIATE. The state is asserted on the EXACT third (final permitted)
        // failure run — not by running well past the boundary, which is what hid the off-by-one-run report.
        NewsTypingRunResult? last = null;
        for (var run = 0; run < 3; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            last = await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(3, harness.Store.Records.Count);

        // Exhaustion is VISIBLE: counted on the run result, degraded in completeness, and named in the
        // decomposition artifact rather than silently draining the budget forever.
        var cohort = Assert.Single(last!.Cohorts);
        Assert.Equal(1, cohort.RetryExhausted);
        Assert.Equal(NewsTypingCompleteness.Failed, cohort.TypingCompletenessByCompany[CompanyId]);
        var (_, markdown, document) = harness.ArtifactStore.Live[^1];
        var company = Assert.Single(document.Companies);
        var companyCohort = Assert.Single(company.Cohorts);
        Assert.Equal(1, companyCohort.RetryExhausted);
        Assert.Contains(
            company.IncompleteReasons,
            r => r.Contains("typing retries exhausted", StringComparison.Ordinal));
        Assert.Contains("retries exhausted 1", markdown);

        // Spec 187 §4: the exhausted observation is NOT also counted as backlog, and the company therefore
        // renders only the MATCHING reason. Pre-187 this row said "untyped remaining 1" AND
        // "retries exhausted 1" for the one observation, over-stating recoverable work.
        Assert.Equal(0, companyCohort.UntypedRemaining);
        Assert.DoesNotContain(
            company.IncompleteReasons, r => r.Contains("typing backlog", StringComparison.Ordinal));

        // Further runs change nothing: the bound is on HOSTED CALLS.
        for (var run = 0; run < 3; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);
    }

    [Fact]
    public async Task RepeatedInvocationWithTheSameRunId_MakesZeroExtraHostedCalls()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("provider always fails", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => ProviderFailure();
        var runId = NextRun(harness);

        await harness.Build().GenerateAsync(runId, CancellationToken.None);
        Assert.Single(harness.Extractor.ObservationsSeen);

        // Rule (a): the SAME run re-invoked skips an observation it already attempted — no model call.
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Build().GenerateAsync(runId, CancellationToken.None);

        Assert.Single(harness.Extractor.ObservationsSeen);
        Assert.Single(harness.Store.Records);
    }

    [Fact]
    public async Task RepeatedStandaloneInvocations_EachCallOnce_PersistDistinctly_AndExhaustAtTheCap()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("provider always fails", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => ProviderFailure();

        for (var invocation = 0; invocation < 5; invocation++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3).GenerateAsync(null, CancellationToken.None);
        }

        // Rule (b): each standalone invocation mints its OWN attempt identity, so the count really advances
        // — and the cap therefore binds the standalone path exactly as it binds the run path.
        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(3, harness.Store.Records.Select(r => r.TypingId).Distinct().Count());
        Assert.All(harness.Store.Records, r => Assert.Null(r.RunId));

        // Spec 187 §3: the ordinal now comes from the durable RESERVATION rather than from a post-hoc
        // record count, and spec 186's outcome identities are byte-unchanged — "standalone",
        // "standalone#2", "standalone#3" in that order.
        var observation = harness.Archive.Observations[0];
        var cohortKey = NewsTypingContract.CohortKey("openai", "test-model-0");
        Assert.Equal(
            [1, 2, 3],
            harness.Ledger.Reservations.Select(r => r.AttemptOrdinal).Order());
        Assert.All(harness.Ledger.Reservations, r => Assert.Null(r.RunId));
        Assert.Equal(
            Enumerable.Range(1, 3)
                .Select(ordinal => NewsTypingRecord.IdentityFor(
                    cohortKey, observation.ObservationId, observation.PayloadHash, null, ordinal))
                .Order()
                .ToList(),
            harness.Store.Records.Select(r => r.TypingId).Order().ToList());
        Assert.All(harness.Store.Records, r => Assert.NotNull(r.AttemptReservationId));
    }

    [Fact]
    public async Task RetryLane_IsBounded_SoAFullFirstAttemptBacklogIsNeverStarved()
    {
        var harness = new Harness();
        var typed = harness.Extractor.Script;
        var failingIds = new HashSet<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var failing = Observation($"failing {i}", AsOf.AddDays(-2));
            failingIds.Add(failing.ObservationId);
            harness.Archive.Observations.Add(failing);
        }

        harness.Extractor.Script = request =>
            failingIds.Contains(request.Observation.ObservationId) ? ProviderFailure() : typed(request);
        await harness.Build().GenerateAsync(NextRun(harness), CancellationToken.None);
        Assert.Equal(4, harness.Extractor.ObservationsSeen.Count);

        // A full first-attempt backlog now arrives beside the four pending retries.
        for (var i = 0; i < 10; i++)
        {
            harness.Archive.Observations.Add(Observation($"fresh {i}", AsOf.AddDays(-3)));
        }

        harness.Extractor.ObservationsSeen.Clear();
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Build(maxNewTypingsPerRun: 6, maxRetryTypingsPerRun: 2)
            .GenerateAsync(NextRun(harness), CancellationToken.None);

        Assert.Equal(6, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(2, harness.Extractor.ObservationsSeen.Count(failingIds.Contains));
    }

    [Fact]
    public async Task UnusedRetryLaneCapacity_FlowsBackToFirstAttempts()
    {
        var harness = new Harness();
        var typed = harness.Extractor.Script;
        var failing = Observation("failing", AsOf.AddDays(-2));
        harness.Archive.Observations.Add(failing);
        harness.Extractor.Script = request =>
            request.Observation.ObservationId == failing.ObservationId
                ? ProviderFailure()
                : typed(request);
        await harness.Build().GenerateAsync(NextRun(harness), CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            harness.Archive.Observations.Add(Observation($"fresh {i}", AsOf.AddDays(-3)));
        }

        harness.Extractor.ObservationsSeen.Clear();
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Build(maxNewTypingsPerRun: 4, maxRetryTypingsPerRun: 3)
            .GenerateAsync(NextRun(harness), CancellationToken.None);

        // One retry pending against a three-slot lane: the two unused slots go to first attempts, so the
        // whole per-run budget is spent (a reservation, never a hold-back).
        Assert.Equal(4, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(1, harness.Extractor.ObservationsSeen.Count(id => id == failing.ObservationId));
    }

    [Fact]
    public async Task FifoRetryOrdering_ReachesAWaitingLaterAttempt_WhileNewFailuresKeepArriving()
    {
        var harness = new Harness();
        var typed = harness.Extractor.Script;
        var failingIds = new HashSet<Guid>();
        harness.Extractor.Script = request =>
            failingIds.Contains(request.Observation.ObservationId) ? ProviderFailure() : typed(request);

        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var observation = Observation($"seed {i}", AsOf.AddDays(-2));
            failingIds.Add(observation.ObservationId);
            seeded.Add(observation.ObservationId);
            harness.Archive.Observations.Add(observation);
        }

        // Run 1: every seeded observation reaches attempt 1.
        await harness.Build(maxNewTypingsPerRun: 6, maxRetryTypingsPerRun: 2)
            .GenerateAsync(NextRun(harness), CancellationToken.None);
        Assert.Equal(5, harness.Extractor.ObservationsSeen.Count);

        // Run 2: the retry lane takes the two oldest — they are now at ATTEMPT 2, and they are the records
        // a fewest-attempts-first lane would starve forever.
        harness.Extractor.ObservationsSeen.Clear();
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Build(maxNewTypingsPerRun: 6, maxRetryTypingsPerRun: 2)
            .GenerateAsync(NextRun(harness), CancellationToken.None);
        var attemptTwo = harness.Extractor.ObservationsSeen.ToList();
        Assert.Equal(2, attemptTwo.Count);

        // Now NEW failures keep arriving every run. The pending snapshot is 5 with a 2-wide lane, so the
        // bound is ceil(5 / 2) = 3 runs — and it must hold for the attempt-2 records too.
        var reached = new HashSet<Guid>();
        for (var run = 0; run < 3; run++)
        {
            // A FULL lane's worth of fresh failures arrives every run: under a fewest-attempts-first lane
            // this replenishing attempt-1 population would consume the lane forever and the attempt-2
            // records would neither retry nor exhaust. Under FIFO they are strictly behind.
            for (var arrivals = 0; arrivals < 2; arrivals++)
            {
                var arrival = Observation($"fresh {run}-{arrivals}", AsOf.AddDays(-1));
                failingIds.Add(arrival.ObservationId);
                harness.Archive.Observations.Add(arrival);
            }

            harness.Extractor.ObservationsSeen.Clear();
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxNewTypingsPerRun: 6, maxRetryTypingsPerRun: 2, maxTypingAttempts: 99)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
            foreach (var id in harness.Extractor.ObservationsSeen)
            {
                reached.Add(id);
            }
        }

        Assert.All(seeded, id => Assert.Contains(id, reached));
        Assert.All(attemptTwo, id => Assert.Contains(id, reached));
    }

    [Fact]
    public async Task ANewPayloadHash_ResetsTheAttemptCount_BecauseItIsADifferentInput()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("provider always fails", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => ProviderFailure();

        for (var run = 0; run < 4; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);

        // The SAME observation re-captured with different content is a different input, not a retry.
        harness.Archive.Observations[0] = harness.Archive.Observations[0] with { PayloadHash = "hash-v2" };
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Build(maxTypingAttempts: 3).GenerateAsync(NextRun(harness), CancellationToken.None);

        Assert.Equal(4, harness.Extractor.ObservationsSeen.Count);
        Assert.Single(harness.Store.Records, r => r.PayloadHash == "hash-v2");
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 187 3 - every hosted call wins a durable PRE-CALL reservation
    //
    // Every assertion below is on the COUNTING extractor's call list, not on stored records: spec 186's
    // bound was expressed over records written AFTER the call, which is exactly why a crash or a failed
    // outcome write could spend a call the bound never saw.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The cohort key of the harness's single reader.</summary>
    private static string CohortKey0 => NewsTypingContract.CohortKey("openai", "test-model-0");

    [Fact]
    public async Task EveryHostedCall_IsPrecededByADurableReservation_AndItsOutcomeLinksBackToIt()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var observation = Observation("Company faces legal scrutiny", AsOf.AddDays(-2));
        harness.Archive.Observations.Add(observation);

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var reservation = Assert.Single(harness.Ledger.Reservations);
        Assert.Equal(NewsTypingAttemptReservation.CurrentSchemaVersion, reservation.SchemaVersion);
        Assert.Equal(CohortKey0, reservation.CohortKey);
        Assert.Equal(observation.ObservationId, reservation.ObservationId);
        Assert.Equal(observation.PayloadHash, reservation.PayloadHash);
        Assert.Equal(1, reservation.AttemptOrdinal);
        Assert.Equal(RunId, reservation.RunId);

        var record = Assert.Single(harness.Store.Records);
        Assert.Equal(reservation.ReservationId, record.AttemptReservationId);
        Assert.Equal(1, record.AttemptOrdinal);
        Assert.Single(harness.Extractor.ObservationsSeen);
    }

    /// <summary>
    /// Spec 187 3's legacy-occupancy MIGRATION read: a pre-187 outcome record carries no
    /// <c>AttemptReservationId</c>, and it must still occupy an attempt. Otherwise every accrued failure in
    /// the live store would silently regain a full budget on the first post-187 run.
    /// </summary>
    [Fact]
    public async Task LegacyOutcomesWithoutAReservation_StillOccupyAttempts()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("provider always fails", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => ProviderFailure();

        for (var run = 0; run < 2; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        Assert.Equal(2, harness.Extractor.ObservationsSeen.Count);

        // Rewrite the two persisted attempts as PRE-187 records (no reservation link) and drop the ledger
        // - that is exactly the shape of the accrued live store on the first post-187 run.
        for (var i = 0; i < harness.Store.Records.Count; i++)
        {
            harness.Store.Records[i] = harness.Store.Records[i] with
            {
                AttemptReservationId = null,
                AttemptOrdinal = null,
            };
        }

        harness.Ledger.Reservations.Clear();

        for (var run = 0; run < 3; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        // Two legacy attempts occupy ordinals 1 and 2, so exactly ONE call remains and it claims ordinal 3.
        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(3, Assert.Single(harness.Ledger.Reservations).AttemptOrdinal);
    }

    /// <summary>
    /// Spec 187 3 step 6: an outcome write that returns <c>false</c> consumes the attempt (the call WAS
    /// made) but produces nothing durable - so it never enters the completed map, never contributes
    /// facts/families, and never reaches the stage-2 judge.
    /// </summary>
    [Fact]
    public async Task AFailedOutcomeWrite_ConsumesTheAttempt_ButNeverFeedsFactsFamiliesOrTheJudge()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Store.FailWrites = true;

        var result = await harness.Build(maxTypingAttempts: 3)
            .GenerateAsync(RunId, CancellationToken.None);

        Assert.Single(harness.Extractor.ObservationsSeen);
        Assert.Empty(harness.Store.Records);
        Assert.Single(harness.Ledger.Reservations);

        var cohort = Assert.Single(result!.Cohorts);
        Assert.Empty(cohort.FactsById);
        Assert.Empty(cohort.Families);
        Assert.Equal(1, cohort.ReservedWithoutOutcome);

        // A storage failure is never reported as ordinary backlog: the company reads Failed this run.
        Assert.Equal(NewsTypingCompleteness.Failed, cohort.TypingCompletenessByCompany[CompanyId]);
        Assert.Empty(Assert.Single(harness.FamilyStore.Snapshots).Snapshot.Families);

        // And the attempt IS consumed: further runs exhaust the budget at three calls, never more.
        for (var run = 0; run < 5; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);
    }

    /// <summary>
    /// The crash seam: the process dies AFTER the reservation and BEFORE the outcome is persisted. Spec
    /// 186's derived count saw nothing and re-called forever; the durable reservation makes the attempt
    /// count even though ZERO outcome records exist.
    /// </summary>
    [Fact]
    public async Task ACrashAfterReservationAndBeforeOutcome_StillConsumesTheAttempt()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Store.ThrowOnWrite = true;

        for (var run = 0; run < 5; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        // Three reservations, three calls, ZERO outcome records - and later runs make no call at all.
        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(3, harness.Ledger.Reservations.Count);
        Assert.Empty(harness.Store.Records);

        // The crash never rolls back or relabels the Radar run: each pass wrote the NAMED failed artifact.
        Assert.NotEmpty(harness.ArtifactStore.Failed);
    }

    /// <summary>
    /// A reservation with no outcome, carried into the NEXT run: the orphan is surfaced as
    /// <c>ReservedWithoutOutcome</c> on the run result and on the decomposition row, and its attempt stays
    /// consumed. The budget can be spent early; it can never be overspent.
    /// </summary>
    [Fact]
    public async Task AReservationWithoutAnOutcome_IsSurfacedInTheNextRun_AndItsAttemptStaysConsumed()
    {
        var harness = new Harness();
        var observation = Observation("Company faces legal scrutiny", AsOf.AddDays(-2));
        harness.Archive.Observations.Add(observation);

        // A previous process claimed ordinal 1 and never recorded an outcome.
        harness.Ledger.PreClaim(
            CohortKey0, observation.ObservationId, observation.PayloadHash, 1, AsOf.AddHours(-1));

        harness.Time.Advance(TimeSpan.FromHours(1));
        var result = await harness.Build(maxTypingAttempts: 3)
            .GenerateAsync(NextRun(harness), CancellationToken.None);

        // The orphan is counted, and this run claims ordinal 2 - never ordinal 1 again.
        var cohort = Assert.Single(result!.Cohorts);
        Assert.Equal(1, cohort.ReservedWithoutOutcome);
        Assert.Single(harness.Extractor.ObservationsSeen);
        Assert.Equal([1, 2], harness.Ledger.Reservations.Select(r => r.AttemptOrdinal).Order());

        var (_, markdown, document) = harness.ArtifactStore.Live[^1];
        Assert.Equal(1, Assert.Single(Assert.Single(document.Companies).Cohorts).ReservedWithoutOutcome);
        Assert.Contains("reserved without outcome 1", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two processes race for one ordinal and exactly one wins. The LOSER skips the observation for this
    /// pass and deliberately does NOT escalate to the following ordinal - that would mint a second
    /// concurrent call for the same input, which is the overspend the ledger exists to prevent.
    /// </summary>
    [Fact]
    public async Task ARefusedReservation_SkipsThePass_AndNeverEscalatesToTheNextOrdinal()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Ledger.RefuseOrdinals.Add(1);

        var result = await harness.Build(maxTypingAttempts: 3)
            .GenerateAsync(RunId, CancellationToken.None);

        // No hosted call, no outcome, and EXACTLY ONE reservation attempt - at ordinal 1.
        Assert.Empty(harness.Extractor.ObservationsSeen);
        Assert.Empty(harness.Store.Records);
        Assert.Equal([1], harness.Ledger.Attempted.Select(r => r.AttemptOrdinal));

        // A refusal is a storage failure, not backlog: the company reads Failed this run.
        Assert.Equal(
            NewsTypingCompleteness.Failed,
            Assert.Single(result!.Cohorts).TypingCompletenessByCompany[CompanyId]);
    }

    /// <summary>
    /// The 3 invariant, stated as one test over a hostile MIX: repeated run ids, standalone invocations,
    /// a failed outcome write and a crash seam. Provider calls for one (cohort, observation, payload)
    /// never exceed <c>MaxTypingAttempts</c>.
    /// </summary>
    [Fact]
    public async Task HostedCalls_NeverExceedMaxTypingAttempts_UnderAnyMixOfRerunsFailuresAndConcurrency()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("provider always fails", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => ProviderFailure();

        var stickyRun = NextRun(harness);
        for (var round = 0; round < 8; round++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));

            // Same run id re-invoked (idempotent), a standalone invocation, and a fresh run - with an
            // outcome-store failure and a crash seam interleaved.
            harness.Store.FailWrites = round % 3 == 1;
            harness.Store.ThrowOnWrite = round % 4 == 3;
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(stickyRun, CancellationToken.None);
            await harness.Build(maxTypingAttempts: 3).GenerateAsync(null, CancellationToken.None);
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        Assert.Equal(3, harness.Extractor.ObservationsSeen.Count);
        Assert.Equal(3, harness.Ledger.Reservations.Count);
        Assert.Equal([1, 2, 3], harness.Ledger.Reservations.Select(r => r.AttemptOrdinal).Order());
    }

    /// <summary>
    /// Same-run idempotency now rests on the RESERVATION as well as the outcome: a run whose outcome write
    /// failed must not re-call on re-invocation of the SAME run id.
    /// </summary>
    [Fact]
    public async Task RepeatedInvocationOfOneRun_MakesNoSecondCall_EvenWhenTheOutcomeWriteFailed()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Store.FailWrites = true;
        var runId = NextRun(harness);

        await harness.Build().GenerateAsync(runId, CancellationToken.None);
        Assert.Single(harness.Extractor.ObservationsSeen);

        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Build().GenerateAsync(runId, CancellationToken.None);

        Assert.Single(harness.Extractor.ObservationsSeen);
        Assert.Single(harness.Ledger.Reservations);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 187 4 - exhaustion is immediate, and disjoint from backlog
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The final permitted attempt failing exhausts the observation in the SAME run. Before spec 187 the
    /// exhausted set was computed before the pass, so this state only appeared on the NEXT run.
    /// </summary>
    [Fact]
    public async Task AFailureOnTheFinalPermittedAttempt_IsExhaustedInThatSameRun()
    {
        var harness = new Harness();
        harness.Archive.Observations.Add(Observation("provider always fails", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => ProviderFailure();

        NewsTypingRunResult? result = null;
        for (var run = 0; run < 2; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            result = await harness.Build(maxTypingAttempts: 2)
                .GenerateAsync(NextRun(harness), CancellationToken.None);

            if (run == 0)
            {
                // After the FIRST of two permitted attempts the observation is still retryable.
                Assert.Equal(0, Assert.Single(result!.Cohorts).RetryExhausted);
            }
        }

        // The second call is the final permitted one, and it exhausts the observation in THIS run's result
        // and THIS run's artifact - no extra run required to discover it.
        Assert.Equal(2, harness.Extractor.ObservationsSeen.Count);
        var cohort = Assert.Single(result!.Cohorts);
        Assert.Equal(1, cohort.RetryExhausted);
        Assert.Equal(NewsTypingCompleteness.Failed, cohort.TypingCompletenessByCompany[CompanyId]);
        var company = Assert.Single(harness.ArtifactStore.Live[^1].Document.Companies);
        Assert.Equal(1, Assert.Single(company.Cohorts).RetryExhausted);
        Assert.Equal(0, Assert.Single(company.Cohorts).UntypedRemaining);
    }

    /// <summary>
    /// The 4 partition, asserted as arithmetic:
    /// <c>ObservationsTyped + ObservationsInsufficientContent + UntypedRemaining + RetryExhausted</c>
    /// reconciles to the eligible in-window population, and the untyped/exhausted sets are DISJOINT (before
    /// spec 187 an exhausted observation was counted in both, so this sum over-counted).
    /// </summary>
    [Fact]
    public async Task UntypedRemainingAndRetryExhausted_AreDisjoint_AndReconcileWithCompletedOutcomes()
    {
        var harness = new Harness();
        var typed = harness.Extractor.Script;
        var doomed = Observation("provider always fails", AsOf.AddDays(-2));
        harness.Archive.Observations.Add(doomed);
        harness.Extractor.Script = request =>
            request.Observation.ObservationId == doomed.ObservationId
                ? ProviderFailure()
                : typed(request);

        // Spend the doomed observation's whole budget.
        for (var run = 0; run < 3; run++)
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await harness.Build(maxTypingAttempts: 3)
                .GenerateAsync(NextRun(harness), CancellationToken.None);
        }

        // Two fresh in-window observations arrive against a one-call budget: one is typed, one is genuine
        // backlog, and the doomed one is exhausted.
        harness.Archive.Observations.Add(Observation("fresh a", AsOf.AddDays(-1)));
        harness.Archive.Observations.Add(Observation("fresh b", AsOf.AddDays(-3)));
        harness.Time.Advance(TimeSpan.FromHours(1));
        await harness.Build(maxNewTypingsPerRun: 1, maxTypingAttempts: 3)
            .GenerateAsync(NextRun(harness), CancellationToken.None);

        var company = Assert.Single(harness.ArtifactStore.Live[^1].Document.Companies);
        var cohort = Assert.Single(company.Cohorts);
        Assert.Equal(3, company.ObservationsInWindow);
        Assert.Equal(1, cohort.ObservationsTyped);
        Assert.Equal(0, cohort.ObservationsInsufficientContent);
        Assert.Equal(1, cohort.UntypedRemaining);
        Assert.Equal(1, cohort.RetryExhausted);
        Assert.Equal(
            company.ObservationsInWindow,
            cohort.ObservationsTyped
                + cohort.ObservationsInsufficientContent
                + cohort.UntypedRemaining
                + cohort.RetryExhausted);

        // Both reasons appear because both categories genuinely exist - but each names its OWN disjoint
        // count, never the same observation twice.
        Assert.Contains(
            company.IncompleteReasons,
            r => r.Contains("typing backlog: 1 observation(s)", StringComparison.Ordinal));
        Assert.Contains(
            company.IncompleteReasons,
            r => r.Contains("typing retries exhausted: 1 observation(s)", StringComparison.Ordinal));
    }

    private sealed class ThrowingArchive : INewsObservationArchive
    {
        public Task<NewsObservationWriteOutcome> WriteAsync(
            NewsObservationRecord record, CancellationToken ct) =>
            throw new InvalidOperationException("archive unavailable");

        public Task<bool> WriteBatchAsync(NewsObservationBatch batch, CancellationToken ct) =>
            throw new InvalidOperationException("archive unavailable");

        public Task<IReadOnlyList<NewsObservationRecord>> GetAllAsync(CancellationToken ct) =>
            throw new InvalidOperationException("archive unavailable");
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 187 §2 — candidate-first typing lanes
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The 18 judgment candidates, fixed and clock-free so every lane assertion is reproducible (AD-3).</summary>
    private static readonly IReadOnlyList<Guid> CandidateCompanyIds =
        [.. Enumerable.Range(0, 18).Select(i => new Guid($"cccccccc-0000-0000-0000-{i:D12}"))];

    /// <summary>The EOSE-shaped noisy candidate: 31 archived observations against everyone else's two.</summary>
    private static readonly Guid NoisyCompanyId = CandidateCompanyIds[0];

    private static readonly IReadOnlyList<Guid> NonCandidateCompanyIds =
        [.. Enumerable.Range(0, 3).Select(i => new Guid($"eeeeeeee-0000-0000-0000-{i:D12}"))];

    private static readonly string ReaderZeroCohortKey =
        new NewsTypingReaderIdentity("reader-0", "openai", "test-model-0").CohortKey;

    /// <summary>
    /// The spec-187 §2 fixture: 18 judgment candidates (the noisy one carrying 31 in-window observations,
    /// three of them the headline shapes that motivated the slice), plus non-candidate window observations
    /// that are all NEWER than every candidate's — which is precisely why the pre-187 global queue spent a
    /// whole live budget on them — plus legacy non-candidate backlog.
    /// </summary>
    private sealed record CandidateFixture(
        NewsJudgmentCandidatePlan Plan,
        IReadOnlyList<NewsObservationRecord> Motivating,
        IReadOnlyList<NewsObservationRecord> CandidateObservations,
        IReadOnlyList<NewsObservationRecord> NonCandidateWindow,
        IReadOnlyList<NewsObservationRecord> NonCandidateBacklog,
        IReadOnlyList<NewsObservationRecord> AllObservations,
        IReadOnlyDictionary<Guid, Guid> CompanyByObservation);

    private static CandidateFixture SeedCandidateFixture(
        Harness harness, int nonCandidateWindow = 300, int nonCandidateBacklog = 40)
    {
        var companyByObservation = new Dictionary<Guid, Guid>();
        var all = new List<NewsObservationRecord>();
        var candidateObservations = new List<NewsObservationRecord>();
        var motivating = new List<NewsObservationRecord>();
        var nonCandidateWindowObservations = new List<NewsObservationRecord>();
        var backlogObservations = new List<NewsObservationRecord>();

        void Add(NewsObservationRecord observation, Guid companyId, List<NewsObservationRecord> bucket)
        {
            harness.Archive.Observations.Add(observation);
            companyByObservation[observation.ObservationId] = companyId;
            all.Add(observation);
            bucket.Add(observation);
        }

        // The three live headlines the judge could not see: a widening loss plus legal scrutiny, legal
        // probes plus losses, and an 11.8% fall after tighter guidance. They sit in the MIDDLE of the noisy
        // company's 31 observations, so the round robin has to reach them rather than the window ordering
        // handing them over by luck.
        string[] motivatingHeadlines =
        [
            "Loss widens as legal scrutiny intensifies",
            "Legal probes mount alongside deeper losses",
            "Shares fall 11.8% after tighter 2026 revenue guidance and a wider Q2 loss",
        ];
        for (var j = 0; j < 31; j++)
        {
            var isMotivating = j is >= 5 and <= 7;
            var observation = Observation(
                isMotivating ? motivatingHeadlines[j - 5] : $"noisy candidate filler {j}",
                AsOf.AddDays(-10).AddMinutes(j),
                NoisyCompanyId);
            Add(observation, NoisyCompanyId, candidateObservations);
            if (isMotivating)
            {
                motivating.Add(observation);
            }
        }

        for (var i = 1; i < CandidateCompanyIds.Count; i++)
        {
            for (var k = 0; k < 2; k++)
            {
                var observation = Observation(
                    $"candidate {i} item {k}",
                    AsOf.AddDays(-11).AddMinutes((i * 10) + k),
                    CandidateCompanyIds[i]);
                Add(observation, CandidateCompanyIds[i], candidateObservations);
            }
        }

        for (var n = 0; n < nonCandidateWindow; n++)
        {
            var companyId = NonCandidateCompanyIds[n % NonCandidateCompanyIds.Count];
            var observation = Observation(
                $"non-candidate window {n}", AsOf.AddDays(-2).AddMinutes(n), companyId);
            Add(observation, companyId, nonCandidateWindowObservations);
        }

        for (var b = 0; b < nonCandidateBacklog; b++)
        {
            var companyId = NonCandidateCompanyIds[b % NonCandidateCompanyIds.Count];
            var observation = Observation(
                $"non-candidate backlog {b}", AsOf.AddDays(-100).AddMinutes(b), companyId);
            Add(observation, companyId, backlogObservations);
        }

        return new CandidateFixture(
            CandidatePlan(),
            motivating,
            candidateObservations,
            nonCandidateWindowObservations,
            backlogObservations,
            all,
            companyByObservation);
    }

    /// <summary>
    /// The plan built through the PRODUCTION path — real strategy sections → the real
    /// <see cref="NewsJudgmentCandidatePlanner"/> → the real spec-179 §3 selector — so the lane tests are
    /// ordered by the same policy the judge is, not by a hand-rolled list.
    /// </summary>
    private static NewsJudgmentCandidatePlan CandidatePlan()
    {
        var sections = new List<StrategyReportSection>();
        for (var offset = 0;
            offset < CandidateCompanyIds.Count;
            offset += NewsRiskCandidateSelector.RowsPerSection)
        {
            var rows = CandidateCompanyIds
                .Skip(offset)
                .Take(NewsRiskCandidateSelector.RowsPerSection)
                .Select((id, index) => NewsRiskTestData.Row(
                    index + 1, id, $"Candidate {offset + index}", $"C{offset + index:D2}"))
                .ToArray();
            sections.Add(NewsRiskTestData.Section(
                $"strategy-{offset}", isPrimary: offset == 0, StrategyPurpose.Research, rows));
        }

        return new NewsJudgmentCandidatePlanner(JudgmentOptions()).Plan(sections);
    }

    private static NewsJudgmentOptions JudgmentOptions() => new(
        outputDirectory: "unused",
        maxCompaniesPerRun: 30,
        maxFamiliesPerJudgment: 50,
        maxJudgmentAttempts: 3,
        presentationJudge: "judge-0",
        presentationExtractor: "reader-0",
        newsSearchCollectorName: "newssearch");

    /// <summary>
    /// A pre-187 (unlinked) failure outcome: it occupies attempt 1, so the observation enters the RETRY
    /// lane with <paramref name="attemptedAtUtc"/> as its FIFO key — the accrued shape spec 187 §3's
    /// legacy-occupancy migration read handles.
    /// </summary>
    private static void SeedPriorFailure(
        Harness harness, NewsObservationRecord observation, DateTimeOffset attemptedAtUtc) =>
        harness.Store.Records.Add(new NewsTypingRecord(
            SchemaVersion: NewsTypingRecord.CurrentSchemaVersion,
            TypingId: Guid.NewGuid(),
            RunId: null,
            ObservationId: observation.ObservationId,
            PayloadHash: observation.PayloadHash,
            CompanyId: observation.CompanyId,
            Ticker: observation.Ticker,
            CaptureMode: observation.CaptureMode,
            ReaderName: "reader-0",
            Provider: "openai",
            ModelId: "test-model-0",
            PromptVersion: NewsTypingContract.PromptVersion,
            ResultSchemaVersion: NewsTypingContract.SchemaVersion,
            TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
            TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
            CohortKey: ReaderZeroCohortKey,
            Relevance: null,
            DerivedPrimaryType: null,
            Facts: [],
            FactsTotal: 0,
            FactsAccepted: 0,
            FactsDropped: 0,
            FactDropReasons: [],
            Status: NewsTypingStatus.ProviderFailure,
            RawResponseHash: null,
            FailureDetail: "seeded prior failure",
            Limits: new NewsTypingLimitsRecord(200, 30, 3, 25, 100),
            ReusedFromTypingId: null,
            CreatedAtUtc: attemptedAtUtc));

    [Fact]
    public async Task CandidateLane_RoundRobin_ReachesEveryCandidateBeforeAnyCandidateTakesASecondSlot()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness);

        await harness.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        // No retries pending, so the first 18 hosted calls ARE round-robin pass 1: one observation for each
        // of the 18 candidates, in plan order. The 31-observation noisy company gets exactly ONE of them —
        // it cannot consume the lane before the others are served.
        var firstPass = harness.Extractor.ObservationsSeen
            .Take(fixture.Plan.Count)
            .Select(id => fixture.CompanyByObservation[id])
            .ToList();
        Assert.Equal(fixture.Plan.CompanyIds, firstPass);
        Assert.Equal(1, firstPass.Count(id => id == NoisyCompanyId));
    }

    [Fact]
    public async Task CandidateLaneCap_BitesRoundRobin_SoOneNoisyCompanyStillCannotConsumeTheLane()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness, nonCandidateWindow: 0, nonCandidateBacklog: 40);

        // A lane exactly as wide as the candidate list: every candidate gets one, nobody gets two.
        var result = await harness
            .Build(maxNewTypingsPerRun: 40, maxRetryTypingsPerRun: 5, maxCandidateTypingsPerRun: 18)
            .GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        var cohort = Assert.Single(result!.Cohorts);
        Assert.Equal(18, cohort.CandidatePrioritySelected);
        Assert.Equal(22, cohort.GeneralSelected);
        Assert.Equal(
            fixture.Plan.CompanyIds,
            harness.Extractor.ObservationsSeen
                .Take(18)
                .Select(id => fixture.CompanyByObservation[id])
                .ToList());
    }

    [Fact]
    public async Task Lanes_AreDisjoint_SoNoObservationIsEverSelectedTwiceInOnePass()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness);

        // Prior failures across BOTH populations, so all three lanes are live in the same pass.
        SeedPriorFailure(harness, fixture.CandidateObservations[0], AsOf.AddDays(-1));
        for (var i = 0; i < 5; i++)
        {
            SeedPriorFailure(harness, fixture.NonCandidateWindow[i], AsOf.AddDays(-1).AddMinutes(i));
        }

        await harness.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        Assert.Equal(
            harness.Extractor.ObservationsSeen.Count,
            harness.Extractor.ObservationsSeen.Distinct().Count());
    }

    [Fact]
    public async Task ThreeLanes_StayInsideTheOnePerRunProviderCallBudget()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness);

        // 30 pending retries against a 25-wide retry lane, 65 candidate first attempts against a 100-wide
        // candidate lane, and a 300-deep global window queue: every lane is over-subscribed or satisfied,
        // and the TOTAL is still exactly one budget.
        for (var i = 0; i < 30; i++)
        {
            SeedPriorFailure(harness, fixture.NonCandidateWindow[i], AsOf.AddDays(-1).AddMinutes(i));
        }

        var result = await harness.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        Assert.Equal(200, harness.Extractor.ObservationsSeen.Count);
        var cohort = Assert.Single(result!.Cohorts);
        Assert.Equal(65, cohort.CandidatePrioritySelected);
        Assert.Equal(200 - 25 - 65, cohort.GeneralSelected);
    }

    [Fact]
    public async Task TheMotivatingCandidateObservations_AreTypedInTheSamePass_WhereasTheGlobalQueueLeavesThemUntyped()
    {
        // The pre-187 live failure, reproduced: the whole 200-call budget goes to the NEWER non-candidate
        // window queue, so every candidate observation — the three motivating headlines included — is still
        // untyped when the judge runs.
        var before = new Harness();
        before.RunStore.Records.Add(RunRecord());
        var beforeFixture = SeedCandidateFixture(before);
        await before.Build().GenerateAsync(RunId, CancellationToken.None);

        Assert.Equal(200, before.Extractor.ObservationsSeen.Count);
        Assert.DoesNotContain(
            before.Extractor.ObservationsSeen,
            id => CandidateCompanyIds.Contains(beforeFixture.CompanyByObservation[id]));

        // The same fixture, the same budget, the same run — with the candidate plan the motivating
        // observations are typed in THIS pass, so the judge sees their facts.
        var after = new Harness();
        after.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(after);
        await after.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        Assert.Equal(200, after.Extractor.ObservationsSeen.Count);
        Assert.All(
            fixture.Motivating,
            m => Assert.Contains(m.ObservationId, after.Extractor.ObservationsSeen));
    }

    [Fact]
    public async Task GeneralLane_StillAdvancesTheLegacyBacklog_OnceTheCandidateLaneIsSatisfied()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness, nonCandidateWindow: 0, nonCandidateBacklog: 40);

        var result = await harness.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        // 65 candidate first attempts + the whole 40-item legacy backlog, inside one 200-call budget:
        // candidate priority reorders work, it never stops the backlog draining.
        Assert.Equal(105, harness.Extractor.ObservationsSeen.Count);
        Assert.All(
            fixture.NonCandidateBacklog,
            o => Assert.Contains(o.ObservationId, harness.Extractor.ObservationsSeen));
        var cohort = Assert.Single(result!.Cohorts);
        Assert.Equal(65, cohort.CandidatePrioritySelected);
        Assert.Equal(40, cohort.GeneralSelected);
    }

    [Fact]
    public async Task RetryLane_StaysGloballyFifo_AndIsNeverReorderedByCandidateStatus()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness, nonCandidateWindow: 0, nonCandidateBacklog: 0);

        // A NON-candidate observation failed FIRST; a candidate's failed later. Spec 186 §2's
        // oldest-last-attempt-first rule therefore puts the non-candidate ahead — retries stay globally
        // fair, so a current leader can never pin failing calls forever.
        var nonCandidate = Observation("non-candidate retry", AsOf.AddDays(-3), NonCandidateCompanyIds[0]);
        harness.Archive.Observations.Add(nonCandidate);
        SeedPriorFailure(harness, nonCandidate, AsOf.AddDays(-5));
        SeedPriorFailure(harness, fixture.CandidateObservations[0], AsOf.AddDays(-4));

        await harness
            .Build(maxNewTypingsPerRun: 10, maxRetryTypingsPerRun: 2, maxCandidateTypingsPerRun: 2)
            .GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        Assert.Equal(
            [nonCandidate.ObservationId, fixture.CandidateObservations[0].ObservationId],
            harness.Extractor.ObservationsSeen.Take(2).ToList());
    }

    [Fact]
    public async Task CandidateStatus_ChangesSelectionOrderOnly_NeverContentValidationCohortOrFamilies()
    {
        // A budget wide enough to type EVERYTHING, run twice over the IDENTICAL observation records — once
        // with the candidate plan, once without.
        var withPlan = new Harness();
        withPlan.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(withPlan, nonCandidateWindow: 20, nonCandidateBacklog: 10);

        var withoutPlan = new Harness();
        withoutPlan.RunStore.Records.Add(RunRecord());
        foreach (var observation in fixture.AllObservations)
        {
            withoutPlan.Archive.Observations.Add(observation);
        }

        await withPlan.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);
        await withoutPlan.Build().GenerateAsync(RunId, CancellationToken.None);

        // The ORDER differs (that is the whole feature) over the SAME selected set …
        Assert.NotEqual(withoutPlan.Extractor.ObservationsSeen, withPlan.Extractor.ObservationsSeen);
        Assert.Equal(
            withoutPlan.Extractor.ObservationsSeen.Order().ToList(),
            withPlan.Extractor.ObservationsSeen.Order().ToList());

        // … and NOTHING else does: every persisted attempt (identity, cohort key, status, validated facts,
        // drop accounting, limits) and every checkpoint family is field-for-field identical. The records
        // are compared through an explicit projection because their list members make the compiler-generated
        // record equality reference-based — a green reference comparison would prove nothing here.
        Assert.Equal(
            withoutPlan.Store.Records.Select(Describe).Order(StringComparer.Ordinal).ToList(),
            withPlan.Store.Records.Select(Describe).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(
            withoutPlan.FamilyStore.Snapshots[^1].Snapshot.Families
                .Select(DescribeFamily).Order(StringComparer.Ordinal).ToList(),
            withPlan.FamilyStore.Snapshots[^1].Snapshot.Families
                .Select(DescribeFamily).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Every content/validation/identity field of one persisted typing attempt, as one comparable string.</summary>
    private static string Describe(NewsTypingRecord record) => string.Join(
        "|",
        record.SchemaVersion,
        record.TypingId,
        record.RunId,
        record.ObservationId,
        record.PayloadHash,
        record.CompanyId,
        record.CohortKey,
        record.PromptVersion,
        record.ResultSchemaVersion,
        record.TaxonomyVersion,
        record.TaxonomyHash,
        record.Relevance,
        record.DerivedPrimaryType,
        record.Status,
        record.RawResponseHash,
        record.FailureDetail,
        record.FactsTotal,
        record.FactsAccepted,
        record.FactsDropped,
        record.AttemptOrdinal,
        record.Limits,
        string.Join(";", record.FactDropReasons),
        string.Join(
            ";",
            record.Facts.Select(f => string.Join(
                ",",
                f.FactId,
                f.Statement,
                f.AssertionStatus,
                f.Attribution,
                f.Confidence,
                string.Join("+", f.EventTypes)))));

    /// <summary>One checkpoint family's identity plus its exact membership, as one comparable string.</summary>
    private static string DescribeFamily(FactFamilyRecord family) => string.Join(
        "|",
        family.FamilyId,
        family.CompanyId,
        family.CaptureMode,
        family.RepresentativeFactId,
        family.MemberCount,
        family.DistinctPublisherCount,
        string.Join(";", family.MemberFactIds.Order()));

    [Fact]
    public async Task WithNoCandidatePlan_SelectionIsExactlyTheSpec186GlobalOrder_AndTheLaneIsSimplyUnused()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var backlogOld = Observation("backlog older", AsOf.AddDays(-90));
        var backlogNewer = Observation("backlog newer", AsOf.AddDays(-60));
        var windowOlder = Observation("window older", AsOf.AddDays(-5));
        var windowNewest = Observation("window newest", AsOf.AddDays(-1));
        harness.Archive.Observations.AddRange([backlogOld, backlogNewer, windowOlder, windowNewest]);

        var result = await harness.Build(maxNewTypingsPerRun: 3)
            .GenerateAsync(RunId, CancellationToken.None, candidatePlan: null);

        // The spec-186 pin, unchanged: window newest-first, then backlog oldest-first, then the cap.
        Assert.Equal(
            [windowNewest.ObservationId, windowOlder.ObservationId, backlogOld.ObservationId],
            harness.Extractor.ObservationsSeen);
        var cohort = Assert.Single(result!.Cohorts);
        Assert.Equal(0, cohort.CandidatePrioritySelected);
        Assert.Equal(3, cohort.GeneralSelected);
    }

    [Fact]
    public async Task WithAnEmptyCandidatePlan_SelectionIsIdenticalToTheNoPlanCase()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var windowOlder = Observation("window older", AsOf.AddDays(-5));
        var windowNewest = Observation("window newest", AsOf.AddDays(-1));
        harness.Archive.Observations.AddRange([windowOlder, windowNewest]);

        var result = await harness.Build()
            .GenerateAsync(RunId, CancellationToken.None, NewsJudgmentCandidatePlan.Empty);

        Assert.Equal(
            [windowNewest.ObservationId, windowOlder.ObservationId],
            harness.Extractor.ObservationsSeen);
        var cohort = Assert.Single(result!.Cohorts);
        Assert.Equal(0, cohort.CandidatePrioritySelected);
        Assert.Equal(2, cohort.GeneralSelected);
    }

    [Fact]
    public async Task Decomposition_RendersThePerCompanyLaneSplit_BesideTheExistingCounters()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness, nonCandidateWindow: 6, nonCandidateBacklog: 0);

        await harness.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);

        var (_, markdown, document) = harness.ArtifactStore.Live[^1];
        Assert.Equal("news-typing-decomposition-v3", document.SchemaVersion);

        var noisy = document.Companies.Single(c => c.CompanyId == NoisyCompanyId);
        var noisyCohort = Assert.Single(noisy.Cohorts);
        Assert.Equal(31, noisyCohort.CandidatePrioritySelected);
        Assert.Equal(0, noisyCohort.GeneralSelected);

        var nonCandidate = document.Companies.Single(c => c.CompanyId == NonCandidateCompanyIds[0]);
        var nonCandidateCohort = Assert.Single(nonCandidate.Cohorts);
        Assert.Equal(0, nonCandidateCohort.CandidatePrioritySelected);
        Assert.Equal(2, nonCandidateCohort.GeneralSelected);

        Assert.Contains("selected this pass: 31 judgment-candidate priority, 0 general", markdown);
    }

    /// <summary>
    /// Spec 187 §2's central claim, end to end: ONE frozen plan drives BOTH passes, so the companies typing
    /// prioritized ARE the companies the judge then judges — same set, same order, byte for byte.
    /// </summary>
    [Fact]
    public async Task TypingPrioritizedCompanies_AreExactlyTheJudgedCompanies_InTheSameOrder()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        var fixture = SeedCandidateFixture(harness, nonCandidateWindow: 20, nonCandidateBacklog: 0);

        var typing = await harness.Build().GenerateAsync(RunId, CancellationToken.None, fixture.Plan);
        Assert.NotNull(typing);

        // Typing side: the order in which candidate companies first received a hosted call.
        var typingPrioritized = harness.Extractor.ObservationsSeen
            .Select(id => fixture.CompanyByObservation[id])
            .Where(CandidateCompanyIds.Contains)
            .Distinct()
            .ToList();

        // Judgment side: the SAME plan instance, consumed rather than reselected.
        var judgmentStore = new JudgmentStoreSpy();
        var judgment = new NewsJudgmentGenerator(
            harness.Archive,
            new NewsJudgmentReaderSet(
            [
                new NewsJudgmentReader(
                    new NewsJudgmentReaderIdentity("judge-0", "openai", "judge-model"),
                    new UnknownTrajectoryJudge()),
            ]),
            judgmentStore,
            JudgmentOptions(),
            harness.Time,
            NullLogger<NewsJudgmentGenerator>.Instance);

        await judgment.GenerateAsync(RunId, fixture.Plan, typing, CancellationToken.None);

        Assert.Equal(fixture.Plan.CompanyIds, typingPrioritized);
        Assert.Equal(typingPrioritized, judgmentStore.Written.Select(r => r.CompanyId).ToList());
    }

    private sealed class JudgmentStoreSpy : INewsJudgmentStore
    {
        public List<NewsJudgmentRecord> Written { get; } = [];

        public Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct)
        {
            Written.Add(record);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NewsJudgmentRecord>>(Written);

        public Task<NewsJudgmentRecord?> FindCompletedAsync(
            string cohortKey, Guid companyId, string familySetHash, CancellationToken ct) =>
            Task.FromResult<NewsJudgmentRecord?>(null);
    }

    /// <summary>The judge's verdict is irrelevant here — only WHICH companies it is handed, and in what order.</summary>
    private sealed class UnknownTrajectoryJudge : INewsJudgmentAnalyzer
    {
        public Task<NewsJudgmentAnalysisOutcome> AnalyzeAsync(
            NewsJudgmentAnalysisRequest request, CancellationToken ct) =>
            Task.FromResult(new NewsJudgmentAnalysisOutcome(
                NewsJudgmentAnalysisFailure.None,
                new NewsJudgmentModelResponse(
                    BusinessTrajectory: "Unknown",
                    ChallengeStrength: 0,
                    Findings: [],
                    Rationale: "No supplied fact establishes a direction."),
                "raw-hash",
                null));
    }


    // ===================================================================================================
    // Spec 187 §7 — provider-call timing and bounded progress visibility.
    //
    // The live gap this closes: typing plus judgment occupied roughly five minutes of a 1h03 run, but
    // nothing in the records or the logs made that visible, so a slow provider and a slow collector were
    // indistinguishable. Every test here uses the FAKE clock's monotonic timestamp — no wall-clock sleep.
    // ===================================================================================================

    /// <summary>Scripts the n-th provider call to take n × 10 ms, so each record's duration is identifiable.</summary>
    private static void ScriptLatency(Harness harness, Func<int, TimeSpan> duration)
    {
        harness.Extractor.Clock = harness.Time;
        harness.Extractor.CallDuration = duration;
    }

    private static List<NewsObservationRecord> ObservationBatch(int count) =>
        Enumerable.Range(0, count)
            .Select(i => Observation($"Company headline {i}", AsOf.AddDays(-1).AddMinutes(-i)))
            .ToList();

    [Fact]
    public async Task ProviderDuration_IsMeasuredMonotonically_AndPersistedOnEveryCallRecord()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.AddRange(ObservationBatch(3));
        ScriptLatency(harness, call => TimeSpan.FromMilliseconds(call * 10));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        // Exactly one duration per hosted call, in call order, measured EXACTLY (the fake clock's
        // TimestampFrequency is TicksPerSecond, so GetElapsedTime is an exact tick subtraction).
        Assert.Equal(
            [10d, 20d, 30d],
            harness.Store.Records
                .OrderBy(r => r.AttemptOrdinal)
                .ThenBy(r => r.CreatedAtUtc)
                .Select(r => r.ProviderDurationMs)
                .ToList());
        Assert.All(harness.Store.Records, r => Assert.NotNull(r.ProviderDurationMs));
    }

    [Fact]
    public async Task ProviderDuration_IsRetained_WhenTheCallFails()
    {
        // A SLOW FAILURE is the case most worth seeing, so a provider/parse/validation failure keeps its
        // measured duration rather than discarding it with the result.
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation("Company faces legal scrutiny", AsOf.AddDays(-2)));
        harness.Extractor.Script = _ => new NewsTypingExtractionOutcome(
            NewsTypingExtractionFailure.ProviderError, null, null, "429 rate limited");
        ScriptLatency(harness, _ => TimeSpan.FromMilliseconds(2500));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var record = Assert.Single(harness.Store.Records);
        Assert.Equal(NewsTypingStatus.ProviderFailure, record.Status);
        Assert.Equal(2500d, record.ProviderDurationMs);
    }

    [Fact]
    public async Task ACacheOnlyPass_MakesNoCall_AndTheSummaryReportsZeroCalls()
    {
        var harness = new Harness();
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.AddRange(ObservationBatch(2));
        ScriptLatency(harness, _ => TimeSpan.FromMilliseconds(40));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);
        Assert.Equal(2, harness.Extractor.ObservationsSeen.Count);

        // A later run over the SAME observations serves everything from the completed-typing cache.
        harness.Logger = new CapturingLogger<NewsTypingGenerator>();
        harness.Time.Advance(TimeSpan.FromDays(1));
        await harness.Build().GenerateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, harness.Extractor.ObservationsSeen.Count);
        Assert.Contains(
            harness.Logger.Entries,
            e => e.Message.Contains(
                "provider timing: 0 provider call(s); no call latency measured this pass",
                StringComparison.Ordinal));

        // Zero calls renders zero calls — never an invented "p50 0.0 ms" — and emits no progress line at
        // all, which would otherwise imply work happened.
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("p50", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("progress:", StringComparison.Ordinal));
    }

    [Theory]
    // Every 25 attempted calls, PLUS the final partial batch.
    [InlineData(24, 1)]   // no boundary reached; one final partial batch.
    [InlineData(25, 1)]   // exactly one boundary; nothing partial left over.
    [InlineData(26, 2)]   // the 25th boundary plus a 1-call partial batch.
    [InlineData(50, 2)]   // two boundaries, nothing partial.
    [InlineData(51, 3)]   // two boundaries plus a 1-call partial batch.
    public async Task ProgressLines_FireAtEveryTwentyFifthCall_PlusTheFinalPartialBatch(
        int observations, int expectedProgressLines)
    {
        var harness = new Harness { Logger = new CapturingLogger<NewsTypingGenerator>() };
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.AddRange(ObservationBatch(observations));
        ScriptLatency(harness, _ => TimeSpan.FromMilliseconds(5));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var progress = harness.Logger!.Entries
            .Where(e => e.Message.Contains("progress:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(expectedProgressLines, progress.Count);
        Assert.All(progress, e => Assert.Equal(LogLevel.Information, e.Level));

        // The LAST progress line always reports the full call count, so the live view is never short.
        Assert.Contains(
            $"{observations}/{observations} call(s) attempted", progress[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressLine_ReportsCompletedSelectedPersistedFailuresElapsedMeanAndMax()
    {
        var harness = new Harness { Logger = new CapturingLogger<NewsTypingGenerator>() };
        harness.RunStore.Records.Add(RunRecord());
        var batch = ObservationBatch(2);
        harness.Archive.Observations.AddRange(batch);

        // Call 1 succeeds in 10 ms; call 2 is a 30 ms provider failure. Selection is window-newest-first,
        // and ObservationBatch backdates each successive item, so call 1 is batch[0].
        harness.Extractor.Script = request => request.Observation.ObservationId == batch[0].ObservationId
            ? new NewsTypingExtractionOutcome(
                NewsTypingExtractionFailure.None,
                new NewsTypingModelResponse(
                    "CompanySpecific",
                    [
                        new NewsTypingModelFact(
                            EventTypes: ["RegulatoryOrLegal"],
                            Statement: request.Observation.Headline,
                            TemporalScope: null,
                            Attribution: "publisher",
                            AssertionStatus: "reported",
                            Confidence: 0.8,
                            Citations: [request.Observation.Headline]),
                    ]),
                "raw-hash",
                null)
            : new NewsTypingExtractionOutcome(
                NewsTypingExtractionFailure.ProviderError, null, null, "boom");
        ScriptLatency(harness, call => TimeSpan.FromMilliseconds(call == 1 ? 10 : 30));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        var progress = Assert.Single(
            harness.Logger!.Entries, e => e.Message.Contains("progress:", StringComparison.Ordinal));
        Assert.Contains("2/2 call(s) attempted", progress.Message, StringComparison.Ordinal);
        Assert.Contains("1 persisted completed typing(s)", progress.Message, StringComparison.Ordinal);
        Assert.Contains(
            "failures 1 provider / 0 parse / 0 validation", progress.Message, StringComparison.Ordinal);
        Assert.Contains("stage elapsed 40 ms", progress.Message, StringComparison.Ordinal);
        Assert.Contains("mean call 20.0 ms", progress.Message, StringComparison.Ordinal);
        Assert.Contains("max call 30.0 ms", progress.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalSummary_ReportsNearestRankPercentilesOverThisPassCallsOnly()
    {
        var harness = new Harness { Logger = new CapturingLogger<NewsTypingGenerator>() };
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.AddRange(ObservationBatch(4));

        // Ascending [10, 20, 30, 1000]: rank(p50) = ceil(0.50 × 4) = 2 ⇒ 20 ms;
        // rank(p95) = ceil(0.95 × 4) = 4 ⇒ 1000 ms. Pinned, so the definition cannot drift.
        var scripted = new[] { 30d, 10d, 1000d, 20d };
        ScriptLatency(harness, call => TimeSpan.FromMilliseconds(scripted[call - 1]));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        Assert.Contains(
            harness.Logger!.Entries,
            e => e.Message.Contains(
                "provider timing: 4 provider call(s); p50 20.0 ms, p95 1000.0 ms, max 1000.0 ms, "
                    + "total 1060.0 ms",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Durations_ChangeNoIdentity_NoOrdering_AndNoSelection()
    {
        // The same fixture, scored twice with WILDLY different latencies. AD-3: timing is observability and
        // must never reach identity, ordering or selection.
        var observations = ObservationBatch(5);

        async Task<(List<Guid> Ids, List<Guid> Calls, List<string> Cohorts, List<Guid> Families)> RunAsync(
            Func<int, TimeSpan> latency)
        {
            var harness = new Harness();
            harness.RunStore.Records.Add(RunRecord());
            harness.Archive.Observations.AddRange(observations);
            ScriptLatency(harness, latency);
            await harness.Build(maxNewTypingsPerRun: 3).GenerateAsync(RunId, CancellationToken.None);
            return (
                harness.Store.Records.Select(r => r.TypingId).ToList(),
                harness.Extractor.ObservationsSeen,
                harness.Store.Records.Select(r => r.CohortKey).ToList(),
                harness.FamilyStore.Snapshots
                    .SelectMany(x => x.Snapshot.Families)
                    .Select(f => f.FamilyId)
                    .ToList());
        }

        var fast = await RunAsync(_ => TimeSpan.FromMilliseconds(1));
        var slow = await RunAsync(call => TimeSpan.FromSeconds(call * 7));

        Assert.Equal(fast.Ids, slow.Ids);
        Assert.Equal(fast.Calls, slow.Calls);
        Assert.Equal(fast.Cohorts, slow.Cohorts);
        Assert.Equal(fast.Families, slow.Families);
        Assert.NotEmpty(fast.Ids);
        Assert.NotEmpty(fast.Families);
    }

    [Fact]
    public async Task Logs_ContainNoModelText_NoApiKey_AndNoEnvironmentVariableValue()
    {
        const string Headline = "RECOGNISABLE-HEADLINE-Company faces legal scrutiny";
        const string ApiKey = "sk-RECOGNISABLE-SECRET-0123456789";

        var harness = new Harness { Logger = new CapturingLogger<NewsTypingGenerator>() };
        harness.RunStore.Records.Add(RunRecord());
        harness.Archive.Observations.Add(Observation(Headline, AsOf.AddDays(-2)));

        // A provider failure whose detail carries BOTH a recognisable secret and recognisable model text —
        // if the timing/progress logging ever echoed either, this fails loudly.
        harness.Extractor.Script = request => new NewsTypingExtractionOutcome(
            NewsTypingExtractionFailure.ProviderError,
            null,
            null,
            $"401 from https://api.example/v1?key={ApiKey} while typing '{request.Observation.Headline}'");
        ScriptLatency(harness, _ => TimeSpan.FromMilliseconds(15));

        await harness.Build().GenerateAsync(RunId, CancellationToken.None);

        Assert.NotEmpty(harness.Logger!.Entries);
        Assert.All(harness.Logger.AllText, text =>
        {
            Assert.DoesNotContain("RECOGNISABLE-HEADLINE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RECOGNISABLE-SECRET", text, StringComparison.Ordinal);
            Assert.DoesNotContain("api.example", text, StringComparison.Ordinal);
        });

        // The failure itself is still durable — it is suppressed from the LOG, not from the record.
        Assert.Contains(harness.Store.Records, r => r.FailureDetail!.Contains(ApiKey, StringComparison.Ordinal));
    }

}
