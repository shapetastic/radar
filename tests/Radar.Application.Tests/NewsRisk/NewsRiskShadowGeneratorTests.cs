using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §§2–7: the shadow generator consumes the EXACT handed-in section instances (no score store, no
/// ranking), freezes selection provenance into every persisted attempt, fails CLOSED on missing
/// sections/coverage, isolates readers from each other's failures, and caches by cohort + ordered
/// input-bundle hash.
/// </summary>
public sealed class NewsRiskShadowGeneratorTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid BatchId = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly DateTimeOffset AsOf = NewsRiskTestData.SelectionAsOf;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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

    private sealed class InMemoryAssessmentStore : INewsRiskAssessmentStore
    {
        public List<NewsRiskAssessmentRecord> Records { get; } = [];

        public Task<bool> WriteAsync(NewsRiskAssessmentRecord record, CancellationToken ct)
        {
            if (Records.All(r => r.AssessmentId != record.AssessmentId))
            {
                Records.Add(record);
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<NewsRiskAssessmentRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NewsRiskAssessmentRecord>>(
                Records.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.AssessmentId).ToList());

        public Task<NewsRiskAssessmentRecord?> FindCompletedAsync(
            string cohortKey, string inputBundleHash, CancellationToken ct) =>
            Task.FromResult(Records
                .Where(r => r.CohortKey == cohortKey
                    && r.InputBundleHash == inputBundleHash
                    && r.IsCompletedAnalysis)
                .OrderByDescending(r => r.CreatedAtUtc)
                .FirstOrDefault());
    }

    private sealed class FakeArtifactStore : INewsRiskArtifactStore
    {
        public NewsRiskLiveDocument? LiveDocument { get; private set; }
        public string? LiveMarkdown { get; private set; }
        public string? FailedReason { get; private set; }

        public Task WriteLiveAsync(
            string asOfDateToken, string markdown, NewsRiskLiveDocument document, CancellationToken ct)
        {
            LiveMarkdown = markdown;
            LiveDocument = document;
            return Task.CompletedTask;
        }

        public Task WriteFailedAsync(string asOfDateToken, string reason, CancellationToken ct)
        {
            FailedReason = reason;
            return Task.CompletedTask;
        }

        public Task WriteEvaluationAsync(string markdown, string csv, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ScriptedAnalyzer : INewsRiskAnalyzer
    {
        private readonly Func<NewsRiskAnalysisRequest, NewsRiskAnalysisOutcome> _respond;

        public ScriptedAnalyzer(Func<NewsRiskAnalysisRequest, NewsRiskAnalysisOutcome> respond)
        {
            _respond = respond;
        }

        public List<NewsRiskAnalysisRequest> Requests { get; } = [];

        public Task<NewsRiskAnalysisOutcome> AnalyzeAsync(
            NewsRiskAnalysisRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static NewsRiskAnalysisOutcome ThesisChallengedOutcome(NewsRiskAnalysisRequest request) =>
        new(
            NewsRiskAnalysisFailure.None,
            new NewsRiskModelResponse(
                Assessment: "ThesisChallenged",
                RiskScore: 66,
                Categories: ["LiquidityOrGoingConcern"],
                Claims:
                [
                    new NewsRiskModelClaim(
                        Category: "LiquidityOrGoingConcern",
                        Severity: "High",
                        Confidence: 0.8,
                        ObservationIds: [request.Articles[0].ObservationId.ToString("D")],
                        Excerpts: [request.Articles[0].Headline]),
                ],
                Rationale: "Coverage reports a going-concern statement."),
            RawResponseHash: "rawhash",
            FailureDetail: null);

    private static NewsRiskAnalysisOutcome NoRiskOutcome() =>
        new(
            NewsRiskAnalysisFailure.None,
            new NewsRiskModelResponse("NoRiskFoundInSuppliedText", null, [], [], "Nothing adverse."),
            RawResponseHash: "rawhash",
            FailureDetail: null);

    private static PipelineRunRecord RunRecord(Guid? batchId) => new(
        Id: RunId,
        CreatedAtUtc: AsOf,
        Collectors: ["newssearch"],
        EvidenceCollected: 0,
        EvidenceNew: 0,
        SignalsExtracted: 0,
        SignalsValid: 0,
        SignalsApproved: 0,
        SignalsNeedingReview: 0,
        CompaniesScored: 1,
        SourcesChecked: 1,
        SourcesFailed: 0,
        ReportId: Guid.NewGuid(),
        NewsObservationBatchId: batchId);

    private static NewsObservationBatch CompleteBatch(Guid companyId) => new(
        BatchId: BatchId,
        RunAsOfUtc: AsOf,
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        FullUniverse: true,
        ObservationsAttempted: 1,
        ObservationsWritten: 1,
        ObservationsCrossRunDeduped: 0,
        ObservationsFailed: 0,
        CaptureProven: true,
        Collectors:
        [
            new NewsObservationCollectorCapture(
                CollectorName: "newssearch",
                CompanyCoverage:
                [
                    new CollectorCompanyCoverage(
                        CompanyId: companyId,
                        ExpectedFeedCount: 1,
                        SuccessfulFeedCount: 1,
                        HitEffectiveResultLimit: false,
                        Issues: []),
                ],
                ProviderFailures: [],
                AnyFeedHitProviderCap: false),
        ]);

    private static IReadOnlyList<StrategyReportSection> Sections(Guid companyId)
    {
        var row = NewsRiskTestData.Row(1, companyId, "Test Co", "TST");
        return [NewsRiskTestData.Section("default", isPrimary: true, StrategyPurpose.Research, row)];
    }

    private static NewsRiskShadowOptions Options(int maxFetched = 0) => new(
        outputDirectory: "data/news-risk",
        lookbackDays: 30,
        maxCompaniesPerRun: 30,
        maxArticlesPerCompany: 12,
        maxFetchedArticlesPerCompany: maxFetched,
        newsSearchCollectorName: "newssearch");

    private static NewsRiskShadowGenerator Build(
        FakeRunStore runStore,
        FakeArchive archive,
        InMemoryAssessmentStore assessments,
        FakeArtifactStore artifacts,
        params NewsRiskReader[] readers) => new(
        runStore,
        archive,
        archive,
        new NewsRiskReaderSet(readers),
        assessments,
        artifacts,
        Options(),
        new FixedTimeProvider(AsOf.AddMinutes(10)),
        NullLogger<NewsRiskShadowGenerator>.Instance);

    private static NewsRiskReader Reader(string name, string model, INewsRiskAnalyzer analyzer) =>
        new(new NewsRiskReaderIdentity(name, "test-provider", model), analyzer);

    [Fact]
    public async Task PersistsFrozenSelectionProvenance_AndTheRunRecordSelectionCutoff()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);

        var sections = Sections(Company);
        await Build(runStore, archive, assessments, artifacts, Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, sections, CancellationToken.None);

        var record = Assert.Single(assessments.Records);
        Assert.Equal(RunId, record.RunId);
        // selectionAsOfUtc is the EXACT durable run record's CreatedAtUtc, not the wall clock.
        Assert.Equal(AsOf, record.SelectionAsOfUtc);
        var selection = Assert.Single(record.Selections);
        Assert.Equal("default", selection.StrategyName);
        Assert.Equal(1, selection.Rank);
        Assert.Equal(sections[0].Rows[0].ScoreSnapshotId, selection.ScoreSnapshotId);
        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, record.Status);
        Assert.Equal(66, record.RiskScore);
        Assert.True(record.CoverageComplete);
        Assert.NotNull(artifacts.LiveDocument);
        Assert.Null(artifacts.LiveDocument!.Diagnostic);
    }

    [Fact]
    public async Task ModelInput_CarriesOnlyCompanyIdentityAndOrderedIdLabelledText()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        var observation = NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1));
        archive.Observations.Add(observation);
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);

        await Build(runStore, archive, new InMemoryAssessmentStore(), new FakeArtifactStore(),
                Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        var request = Assert.Single(analyzer.Requests);
        // The request TYPE carries company name/ticker + articles and nothing else; assert the values are
        // exactly the supplied point-in-time text — no score, no rank, no price, no outcome exists on the
        // contract at all (rank remains OUTPUT provenance on the record, never prompt content).
        Assert.Equal("Test Co", request.CompanyName);
        Assert.Equal("TST", request.Ticker);
        var article = Assert.Single(request.Articles);
        Assert.Equal(observation.ObservationId, article.ObservationId);
        Assert.Equal(observation.Headline, article.Headline);
    }

    [Fact]
    public async Task NullSections_WriteTheNamedNoLiveStrategySectionsDiagnostic()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var artifacts = new FakeArtifactStore();
        var assessments = new InMemoryAssessmentStore();

        await Build(runStore, new FakeArchive(), assessments, artifacts, Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, null, CancellationToken.None);

        Assert.NotNull(artifacts.LiveDocument);
        Assert.Equal(NewsRiskLiveDocument.NoLiveStrategySections, artifacts.LiveDocument!.Diagnostic);
        Assert.Empty(artifacts.LiveDocument.Companies);
        Assert.Empty(assessments.Records); // rows are never invented
        Assert.Empty(analyzer.Requests);
    }

    [Fact]
    public async Task IncompleteCoverage_RecordsIncompleteCoverage_CallsNoModel_AndCannotRenderNoRisk()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(batchId: null)); // no batch manifest at all → unproven
        var archive = new FakeArchive();
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "headline", AsOf.AddDays(-1)));
        var analyzer = new ScriptedAnalyzer(_ => NoRiskOutcome());
        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();

        await Build(runStore, archive, assessments, artifacts, Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        Assert.Empty(analyzer.Requests); // fail closed: no model call over unproven coverage
        var record = Assert.Single(assessments.Records);
        Assert.Equal(NewsRiskAssessmentStatus.IncompleteCoverage, record.Status);
        Assert.False(record.CoverageComplete);
        var result = Assert.Single(Assert.Single(artifacts.LiveDocument!.Companies).ReaderResults);
        Assert.NotEqual(NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText, result.Status);
    }

    [Fact]
    public async Task EmptyBundleWithCompleteCoverage_RecordsNoContent_NeverNoRisk()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) }; // coverage fine, zero articles
        var analyzer = new ScriptedAnalyzer(_ => NoRiskOutcome());
        var assessments = new InMemoryAssessmentStore();

        await Build(runStore, archive, assessments, new FakeArtifactStore(), Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        Assert.Empty(analyzer.Requests);
        Assert.Equal(NewsRiskAssessmentStatus.NoContent, Assert.Single(assessments.Records).Status);
    }

    [Fact]
    public async Task OneReadersProviderFailure_NeverBlocksTheOther_AndBothCohortsPersist()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var failing = new ScriptedAnalyzer(_ => new NewsRiskAnalysisOutcome(
            NewsRiskAnalysisFailure.ProviderError, null, null, "host unreachable"));
        var healthy = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();

        await Build(
                runStore, archive, assessments, artifacts,
                Reader("broken", "model-a", failing), Reader("healthy", "model-b", healthy))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        Assert.Equal(2, assessments.Records.Count);
        var brokenRecord = Assert.Single(assessments.Records, r => r.ReaderName == "broken");
        Assert.Equal(NewsRiskAssessmentStatus.ProviderFailure, brokenRecord.Status);
        var healthyRecord = Assert.Single(assessments.Records, r => r.ReaderName == "healthy");
        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, healthyRecord.Status);
        // Two cohorts — never merged; and the artifact renders both readers with no combined verdict.
        Assert.NotEqual(brokenRecord.CohortKey, healthyRecord.CohortKey);
        Assert.Equal(2, Assert.Single(artifacts.LiveDocument!.Companies).ReaderResults.Count);
        // Per-reader rendering, labelled by reader name AND exact model id — and no combined verdict
        // anywhere (the document TYPE holds only per-reader results; nothing merged exists to render).
        Assert.Contains("### Reader broken (test-provider:model-a)", artifacts.LiveMarkdown!);
        Assert.Contains("### Reader healthy (test-provider:model-b)", artifacts.LiveMarkdown!);
    }

    [Fact]
    public async Task SameCohortAndBundle_ReusesTheCachedAssessment_InsteadOfCallingTheModelAgain()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var assessments = new InMemoryAssessmentStore();

        var generator = Build(
            runStore, archive, assessments, new FakeArtifactStore(), Reader("a", "model-a", analyzer));
        await generator.GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        // A SECOND run (new run id, same cohort + same ordered input bundle) must hit the cache.
        var secondRunId = Guid.NewGuid();
        runStore.Records.Insert(0, RunRecord(BatchId) with { Id = secondRunId });
        await generator.GenerateAsync(secondRunId, Sections(Company), CancellationToken.None);

        Assert.Single(analyzer.Requests); // one model call total
        Assert.Equal(2, assessments.Records.Count); // but EVERY attempt persisted, per-run provenance intact
        var reused = Assert.Single(assessments.Records, r => r.RunId == secondRunId);
        Assert.Equal(assessments.Records.Single(r => r.RunId == RunId).AssessmentId, reused.ReusedFromAssessmentId);
        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, reused.Status);
    }

    [Fact]
    public async Task ADifferentModel_IsADistinctCohortAndCacheEntry()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var analyzerA = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var analyzerB = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var assessments = new InMemoryAssessmentStore();

        await Build(
                runStore, archive, assessments, new FakeArtifactStore(),
                Reader("a", "model-a", analyzerA), Reader("b", "model-b", analyzerB))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        // Both models were genuinely called (no cross-model cache reuse) and produced distinct records.
        Assert.Single(analyzerA.Requests);
        Assert.Single(analyzerB.Requests);
        Assert.Equal(2, assessments.Records.Select(r => r.CohortKey).Distinct().Count());
        Assert.Equal(2, assessments.Records.Select(r => r.AssessmentId).Distinct().Count());
        Assert.All(assessments.Records, r => Assert.Null(r.ReusedFromAssessmentId));
    }

    [Fact]
    public async Task MissingRunRecord_WritesTheNamedFailedArtifact_AndAssessesNothing()
    {
        var runStore = new FakeRunStore(); // no record for RunId
        var artifacts = new FakeArtifactStore();
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);

        await Build(runStore, new FakeArchive(), new InMemoryAssessmentStore(), artifacts,
                Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        Assert.NotNull(artifacts.FailedReason);
        Assert.Contains("RunRecordNotFound", artifacts.FailedReason);
        Assert.Empty(analyzer.Requests);
    }

    [Fact]
    public async Task NullRunId_WritesTheNamedFailedArtifact()
    {
        var artifacts = new FakeArtifactStore();

        await Build(new FakeRunStore(), new FakeArchive(), new InMemoryAssessmentStore(), artifacts,
                Reader("a", "model-a", new ScriptedAnalyzer(ThesisChallengedOutcome)))
            .GenerateAsync(null, Sections(Company), CancellationToken.None);

        Assert.NotNull(artifacts.FailedReason);
        Assert.Contains("RunIdUnavailable", artifacts.FailedReason);
    }

    [Fact]
    public async Task StoredFetchedBody_AnchorsTheAssessmentCutoffAtTheActualRetrieval()
    {
        var fetchAt = AsOf.AddHours(6);
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(
            Company, "Test Co flags doubt", AsOf.AddDays(-1),
            articleFetch: new NewsArticleFetchResult(
                NewsArticleFetchOutcome.Fetched, fetchAt, 0, null, 200, "text/html",
                false, "vt-1", "bh", "body text", "policy-1")));
        var assessments = new InMemoryAssessmentStore();

        var generator = new NewsRiskShadowGenerator(
            runStore,
            archive,
            archive,
            new NewsRiskReaderSet([Reader("a", "model-a", new ScriptedAnalyzer(ThesisChallengedOutcome))]),
            assessments,
            new FakeArtifactStore(),
            new NewsRiskShadowOptions("data/news-risk", 30, 30, 12, 3, "newssearch"),
            new FixedTimeProvider(AsOf.AddMinutes(10)),
            NullLogger<NewsRiskShadowGenerator>.Instance);
        await generator.GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        var record = Assert.Single(assessments.Records);
        Assert.Equal(fetchAt, record.AssessmentCutoffUtc); // actual retrieval instant, never selection time
        Assert.Equal(AsOf, record.SelectionAsOfUtc);
        var observation = Assert.Single(record.Observations);
        Assert.True(observation.BodySupplied);
        Assert.Equal(fetchAt, observation.BodyRetrievedAtUtc);
    }
}
