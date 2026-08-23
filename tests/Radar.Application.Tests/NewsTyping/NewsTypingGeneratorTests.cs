using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsTyping;
using Radar.Application.Pipeline;

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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

        public Task<bool> WriteAsync(NewsTypingRecord record, CancellationToken ct)
        {
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

        public InMemoryFamilyStore FamilyStore { get; } = new();

        public InMemoryArtifactStore ArtifactStore { get; } = new();

        public ScriptedExtractor Extractor { get; } = new();

        public TimeProvider Time { get; } = new FixedTimeProvider(NewsTypingTestData.AsOf.AddMinutes(10));

        public NewsTypingGenerator Build(int maxNewTypingsPerRun = 200, int readers = 1)
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
                FamilyStore,
                ArtifactStore,
                new NewsTypingOptions("data/news-typing", maxNewTypingsPerRun, lookbackDays: 30),
                Time,
                NullLogger<NewsTypingGenerator>.Instance);
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
    }

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
            harness.FamilyStore,
            harness.ArtifactStore,
            new NewsTypingOptions("data/news-typing", 10, 30),
            harness.Time,
            NullLogger<NewsTypingGenerator>.Instance);

        await generator.GenerateAsync(RunId, CancellationToken.None);

        var (_, reason) = Assert.Single(harness.ArtifactStore.Failed);
        Assert.Contains("InvalidOperationException", reason);
        Assert.Empty(harness.ArtifactStore.Live);
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
}
