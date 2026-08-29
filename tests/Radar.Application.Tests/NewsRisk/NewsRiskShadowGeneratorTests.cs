using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.Pipeline;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Storage;

namespace Radar.Application.Tests.NewsRisk;

/// <summary>
/// Spec 179 §§2–7 as amended by spec 182: the shadow generator consumes the EXACT handed-in section
/// instances (no score store, no ranking), freezes selection provenance into every persisted attempt,
/// records the three completeness dimensions on every attempt WITHOUT ever blocking a reader on them
/// (completeness gates absence claims, never presence claims), isolates readers from each other's
/// failures, and caches ONLY the raw verdict by cohort + ordered input-bundle hash.
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

        public Task<DurableWriteResult> WriteAsync(PipelineRunRecord record, CancellationToken ct) =>
            Task.FromResult(DurableWriteResult.Succeeded("(unused)"));

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

    private sealed class ScriptedContentReader(Func<string, NewsArticleFetchResult> respond)
        : INewsArticleContentReader
    {
        public List<string> Urls { get; } = [];

        public Task<NewsArticleFetchResult> FetchAsync(string url, CancellationToken ct)
        {
            Urls.Add(url);
            return Task.FromResult(respond(url));
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
        Assert.Equal(NewsRiskArchiveCapture.Proven, record.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Complete, record.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Complete, record.AssessmentBundle);
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
    public async Task DegradedCoverage_NeverBlocksTheReader_AndRecordsUnprovenDimensions()
    {
        // Spec 182 §1: completeness is required for ABSENCE claims, never PRESENCE claims — with no batch
        // manifest at all (both coverage dimensions Unproven) the reader still assesses the supplied text.
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(batchId: null)); // no batch manifest at all → unproven
        var archive = new FakeArchive();
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();

        await Build(runStore, archive, assessments, artifacts, Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        Assert.Single(analyzer.Requests); // the model IS called over one qualifying article
        var record = Assert.Single(assessments.Records);
        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, record.Status);
        Assert.Equal(NewsRiskArchiveCapture.Unproven, record.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Unproven, record.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Complete, record.AssessmentBundle); // independent dimension
        Assert.Contains("archive-batch-unavailable", Assert.Single(record.CoverageIssues));
        // The live result carries a degraded-dimension warning — a stated caveat, never a suppression.
        // Unproven-only degradation reads as "not proven", never overstated as "known incomplete".
        var result = Assert.Single(Assert.Single(artifacts.LiveDocument!.Companies).ReaderResults);
        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("supplied text completeness is not proven", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("known incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EoseShape_TruncatedSearchAndCappedBundle_StillAssesses_AndRecordsEachDimensionIndependently()
    {
        // The motivating live failure (spec 182 overview): risk-laden supplied text under a truncated
        // provider enumeration and a capped bundle. The readers MUST look, and the record must carry
        // Truncated / Capped / Proven independently.
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var truncatedBatch = CompleteBatch(Company) with
        {
            Collectors =
            [
                new NewsObservationCollectorCapture(
                    CollectorName: "newssearch",
                    CompanyCoverage:
                    [
                        new CollectorCompanyCoverage(
                            CompanyId: Company,
                            ExpectedFeedCount: 1,
                            SuccessfulFeedCount: 1,
                            HitEffectiveResultLimit: true,
                            Issues: []),
                    ],
                    ProviderFailures: [],
                    AnyFeedHitProviderCap: true),
            ],
        };
        var archive = new FakeArchive { Batch = truncatedBatch };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Legal Scrutiny Mounts", AsOf.AddDays(-1)));
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Losses Rattle Traders", AsOf.AddDays(-2)));
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Wider Q2 Loss", AsOf.AddDays(-3)));
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();

        var generator = new NewsRiskShadowGenerator(
            runStore,
            archive,
            archive,
            new NewsRiskReaderSet([Reader("a", "model-a", analyzer)]),
            assessments,
            artifacts,
            new NewsRiskShadowOptions("data/news-risk", 30, 30, maxArticlesPerCompany: 2, 0, "newssearch"),
            new FixedTimeProvider(AsOf.AddMinutes(10)),
            NullLogger<NewsRiskShadowGenerator>.Instance);
        await generator.GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        Assert.Single(analyzer.Requests); // the reader IS called
        var record = Assert.Single(assessments.Records);
        Assert.Equal(NewsRiskAssessmentStatus.ThesisChallenged, record.Status);
        Assert.Equal(NewsRiskArchiveCapture.Proven, record.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Truncated, record.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Capped, record.AssessmentBundle);
        Assert.Equal(2, record.Observations.Count); // capped at 2 of 3 qualifying

        // Validated claims render as ThesisChallenged with all three dimensions stated beside them.
        var company = Assert.Single(artifacts.LiveDocument!.Companies);
        Assert.Equal(NewsRiskArchiveCapture.Proven, company.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Truncated, company.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Capped, company.AssessmentBundle);
        Assert.Equal(3, company.QualifyingArticleCount);
        Assert.Contains("Status: **ThesisChallenged**", artifacts.LiveMarkdown!);
        Assert.Contains(
            "Completeness: archive capture Proven · search enumeration Truncated · assessment bundle "
                + "Capped (2 supplied of 3 qualifying available)",
            artifacts.LiveMarkdown!);
        var result = Assert.Single(company.ReaderResults);
        Assert.Contains(
            result.Warnings,
            w => w.Contains("search enumeration Truncated", StringComparison.Ordinal)
                && w.Contains("bundle capped at 2 of 3 qualifying available", StringComparison.Ordinal)
                && w.Contains("supplied text is known incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CappedBundle_UnderCompleteSearchEnumeration_ShowsTheDimensionsAreIndependent()
    {
        // Complete provider coverage does NOT mean complete model input (spec 182 §2): with a perfectly
        // complete newssearch enumeration, a bundle cap below the qualifying count still records Capped.
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "First story", AsOf.AddDays(-1)));
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Second story", AsOf.AddDays(-2)));
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Third story", AsOf.AddDays(-3)));
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var assessments = new InMemoryAssessmentStore();

        var generator = new NewsRiskShadowGenerator(
            runStore,
            archive,
            archive,
            new NewsRiskReaderSet([Reader("a", "model-a", analyzer)]),
            assessments,
            new FakeArtifactStore(),
            new NewsRiskShadowOptions("data/news-risk", 30, 30, maxArticlesPerCompany: 2, 0, "newssearch"),
            new FixedTimeProvider(AsOf.AddMinutes(10)),
            NullLogger<NewsRiskShadowGenerator>.Instance);
        await generator.GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        var record = Assert.Single(assessments.Records);
        Assert.Equal(NewsRiskSearchEnumeration.Complete, record.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Capped, record.AssessmentBundle);
        Assert.Equal(NewsRiskArchiveCapture.Proven, record.ArchiveCapture);
    }

    [Fact]
    public async Task CachedRawVerdict_ReplayedUnderDifferentDimensions_CarriesTheCurrentRunsDimensions()
    {
        // Run 1: complete coverage. Run 2: SAME bundle (cache hit) but no batch manifest — the reused
        // record must carry run 2's degraded dimensions, never the cached run's (spec 182 §3).
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var analyzer = new ScriptedAnalyzer(_ => NoRiskOutcome());
        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();

        var generator = Build(runStore, archive, assessments, artifacts, Reader("a", "model-a", analyzer));
        await generator.GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        var secondRunId = Guid.NewGuid();
        runStore.Records.Insert(0, RunRecord(batchId: null) with { Id = secondRunId });
        archive.Batch = null;
        await generator.GenerateAsync(secondRunId, Sections(Company), CancellationToken.None);

        Assert.Single(analyzer.Requests); // cache hit — one model call total
        var first = Assert.Single(assessments.Records, r => r.RunId == RunId);
        Assert.Equal(NewsRiskArchiveCapture.Proven, first.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Complete, first.SearchEnumeration);
        var reused = Assert.Single(assessments.Records, r => r.RunId == secondRunId);
        Assert.Equal(first.AssessmentId, reused.ReusedFromAssessmentId);
        Assert.Equal(NewsRiskAssessmentStatus.NoRiskFoundInSuppliedText, reused.Status); // raw verdict reused
        Assert.Equal(NewsRiskArchiveCapture.Unproven, reused.ArchiveCapture); // THIS run's dimensions
        Assert.Equal(NewsRiskSearchEnumeration.Unproven, reused.SearchEnumeration);
        // And the rendered presentation is derived from THIS run's dimensions too — unproven-only
        // degradation reads as "not proven", never overstated as "known incomplete".
        Assert.Contains("Supplied text is not proven complete", artifacts.LiveMarkdown!);
        Assert.Contains("archive capture Unproven", artifacts.LiveMarkdown!);
    }

    [Fact]
    public async Task BodyAttachment_ProceedsUnderDegradedCoverage_AndRecordsFetchOutcomes()
    {
        // Spec 182 §1: the body-fetch gate no longer requires complete coverage — a fetched body is more
        // information, and more information never requires completeness.
        var fetchAt = AsOf.AddHours(2);
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(batchId: null)); // degraded: no batch manifest
        var archive = new FakeArchive();
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Second headline", AsOf.AddDays(-2)));
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);
        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();
        var contentReader = new ScriptedContentReader(url =>
            url.Contains("news.google.com", StringComparison.Ordinal)
                ? new NewsArticleFetchResult(
                    NewsArticleFetchOutcome.Fetched, fetchAt, 0, null, 200, "text/html",
                    false, "vt-1", "bh", "fetched body", "policy-1")
                : new NewsArticleFetchResult(
                    NewsArticleFetchOutcome.Paywalled, fetchAt, 0, null, 403, null,
                    false, null, null, null, "policy-1"));

        var generator = new NewsRiskShadowGenerator(
            runStore,
            archive,
            archive,
            new NewsRiskReaderSet([Reader("a", "model-a", analyzer)]),
            assessments,
            artifacts,
            new NewsRiskShadowOptions("data/news-risk", 30, 30, 12, maxFetchedArticlesPerCompany: 1, "newssearch"),
            new FixedTimeProvider(AsOf.AddMinutes(10)),
            NullLogger<NewsRiskShadowGenerator>.Instance,
            contentReader);
        await generator.GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        Assert.Single(contentReader.Urls); // the fetch ran despite degraded coverage (fetched cap 1)
        var record = Assert.Single(assessments.Records);
        Assert.Equal(NewsRiskArchiveCapture.Unproven, record.ArchiveCapture);
        var fetched = Assert.Single(record.Observations, o => o.BodySupplied);
        Assert.Equal(fetchAt, fetched.BodyRetrievedAtUtc);
        Assert.Equal(fetchAt, record.AssessmentCutoffUtc); // cutoff still moves to the actual retrieval
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
        var record = Assert.Single(assessments.Records);
        Assert.Equal(NewsRiskAssessmentStatus.NoContent, record.Status);
        // The dimensions are recorded on EVERY persisted attempt — NoContent included (spec 182 §2).
        Assert.Equal(NewsRiskArchiveCapture.Proven, record.ArchiveCapture);
        Assert.Equal(NewsRiskSearchEnumeration.Complete, record.SearchEnumeration);
        Assert.Equal(NewsRiskAssessmentBundle.Complete, record.AssessmentBundle);
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
        // Both readers assess independently under IDENTICAL recorded dimensions (per company per run).
        Assert.Equal(brokenRecord.ArchiveCapture, healthyRecord.ArchiveCapture);
        Assert.Equal(brokenRecord.SearchEnumeration, healthyRecord.SearchEnumeration);
        Assert.Equal(brokenRecord.AssessmentBundle, healthyRecord.AssessmentBundle);
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

    // ---------------------------------------------------------------------------------------------------
    // Spec 185 §5 — the handed-in judgment run result is embedded per company (never re-read, never pooled)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandedInJudgment_IsEmbeddedOnTheCompany_WithThePresentationMarker()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var artifacts = new FakeArtifactStore();

        var judgmentRecord = new Radar.Application.NewsRisk.Judgment.NewsJudgmentRecord(
            SchemaVersion: Radar.Application.NewsRisk.Judgment.NewsJudgmentRecord.CurrentSchemaVersion,
            JudgmentId: Guid.NewGuid(),
            RunId: RunId,
            CompanyId: Company,
            CompanyName: "Test Co",
            Ticker: "TST",
            JudgeName: "judge-a",
            Provider: "openai",
            ModelId: "judge-model",
            PromptVersion: Radar.Application.NewsRisk.Judgment.NewsJudgmentContract.PromptVersion,
            ResultSchemaVersion: Radar.Application.NewsRisk.Judgment.NewsJudgmentContract.SchemaVersion,
            Stage1CohortKey: "s1",
            TaxonomyVersion: "news-event-taxonomy-v1",
            TaxonomyHash: "hash",
            FamilyBuilderIdentity: "fact-family-v1",
            CohortKey: "cohort",
            FamilySetHash: "fsh",
            Families: [],
            ArchiveCapture: NewsRiskArchiveCapture.Proven,
            SearchEnumeration: NewsRiskSearchEnumeration.Complete,
            ObservationSupply: NewsRiskAssessmentBundle.Complete,
            TypingCompleteness: Radar.Application.NewsTyping.NewsTypingCompleteness.Complete,
            FamilyBundle: Radar.Application.NewsRisk.Judgment.NewsJudgmentFamilyBundle.Complete,
            CoverageIssues: [],
            Status: Radar.Application.NewsRisk.Judgment.NewsJudgmentStatus.Judged,
            BusinessTrajectory: Radar.Application.NewsRisk.Judgment.NewsJudgmentTrajectory.Deteriorating,
            ChallengeStrength: 70,
            Findings:
            [
                new Radar.Application.NewsRisk.Judgment.NewsJudgmentValidatedFinding(
                    NewsRiskCategory.RegulatoryOrLegalSetback, NewsRiskSeverity.High, 0.8,
                    [Guid.NewGuid()], null),
            ],
            Rationale: null,
            FindingsTotal: 1,
            FindingsAccepted: 1,
            FindingsDropped: 0,
            FindingDropReasons: [],
            RawResponseHash: "raw",
            FailureDetail: null,
            Limits: new Radar.Application.NewsRisk.Judgment.NewsJudgmentLimitsRecord(30, 50),
            ReusedFromJudgmentId: null,
            CreatedAtUtc: AsOf);
        var judgment = new Radar.Application.NewsRisk.Judgment.NewsJudgmentRunResult(
            Judgments: [judgmentRecord],
            Markers: new NewsJudgmentMarkerReportModel(
                JudgmentPending: false,
                Markers: new Dictionary<Guid, NewsJudgmentLeaderMarker>
                {
                    [Company] = new(
                        NewsJudgmentMarkerState.Challenged,
                        ChallengeSummary: "regulatory-or-legal-setback, high"),
                }),
            Stage1FactsDroppedByCohort: new Dictionary<string, int> { ["s1"] = 3 });

        await Build(
                runStore, archive, new InMemoryAssessmentStore(), artifacts,
                Reader("a", "model-a", new ScriptedAnalyzer(ThesisChallengedOutcome)))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None, judgment);

        var company = Assert.Single(artifacts.LiveDocument!.Companies);
        var embedded = Assert.Single(company.Judgments!);
        Assert.Equal("judge-a", embedded.JudgeName);
        Assert.Equal(3, embedded.Stage1FactsDroppedInWindow);
        Assert.Equal(
            Radar.Application.NewsRisk.Judgment.NewsJudgmentTrajectory.Deteriorating,
            embedded.BusinessTrajectory);
        Assert.Equal(
            "⚠ challenged (regulatory-or-legal-setback, high)", company.JudgmentMarker);
    }

    [Fact]
    public async Task NoJudgment_LeavesTheCompanyExactlyAsBefore()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(Company, "Test Co flags doubt", AsOf.AddDays(-1)));
        var artifacts = new FakeArtifactStore();

        await Build(
                runStore, archive, new InMemoryAssessmentStore(), artifacts,
                Reader("a", "model-a", new ScriptedAnalyzer(ThesisChallengedOutcome)))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        var company = Assert.Single(artifacts.LiveDocument!.Companies);
        Assert.Null(company.Judgments);
        Assert.Null(company.JudgmentMarker);
    }

    // ---------------------------------------------------------------------------------------------
    // SPEC 195 §2 — the pre-collapse syndication measurement reaches the live artifact.
    //
    // Spec 193 computed the counts onto the TRANSIENT bundle and nothing read them, so after the pass forty
    // syndicated copies of one story were indistinguishable from one article. The values recorded here are
    // THIS run's freshly-built bundle's, never a cached assessment record's: the surviving supplied articles
    // (and therefore BundleHash) can be identical while syndication breadth has changed, and reusing a
    // cached record's breadth would display an old run's enumeration as current.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// N syndicated copies of one headline across M publishers produce N−1 collapsed / M publishers in the
    /// transient bundle, in the v4 live document AND in the rendered markdown — the same numbers at all
    /// three places, so "the artifact names the same values" is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task SyndicationCounts_ReachTheLiveDocumentAndMarkdown_WithTheBundlesOwnValues()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };

        // N = 4 copies of ONE story across M = 3 distinct publishers ⇒ 3 collapsed / 3 publishers.
        archive.Observations.Add(NewsRiskTestData.Observation(
            Company, "Test Co wins contract - Reuters", AsOf.AddDays(-1), publisher: "Reuters"));
        archive.Observations.Add(NewsRiskTestData.Observation(
            Company, "Test Co wins contract - Yahoo Finance", AsOf.AddDays(-2), publisher: "Yahoo Finance"));
        archive.Observations.Add(NewsRiskTestData.Observation(
            Company, "Test Co wins contract - MarketWatch", AsOf.AddDays(-3), publisher: "MarketWatch"));
        archive.Observations.Add(NewsRiskTestData.Observation(
            Company, "Test Co wins contract - Reuters", AsOf.AddDays(-4), publisher: "Reuters"));

        var artifacts = new FakeArtifactStore();
        await Build(
                runStore, archive, new InMemoryAssessmentStore(), artifacts,
                Reader("a", "model-a", new ScriptedAnalyzer(ThesisChallengedOutcome)))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        var company = Assert.Single(artifacts.LiveDocument!.Companies);

        // One survivor supplied to the reader — the collapse itself is unchanged by spec 195.
        Assert.Single(company.Articles);

        Assert.Equal(3, company.SyndicatedDuplicateCount);
        Assert.Equal(3, company.SyndicatedDistinctPublisherCount);
        Assert.Equal("news-risk-live-v5", artifacts.LiveDocument.SchemaVersion);

        Assert.Contains(
            "Syndication before collapse: 3 duplicate cop", artifacts.LiveMarkdown!, StringComparison.Ordinal);
        Assert.Contains(
            "across 3 distinct publisher(s)", artifacts.LiveMarkdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// A company with nothing to collapse records a MEASURED ZERO — not <c>null</c>. The distinction is the
    /// whole point of the trailing-nullable member: "this run enumerated and nothing collapsed" is a fact,
    /// and "not recorded" is a different one.
    /// </summary>
    [Fact]
    public async Task NoSyndication_RecordsAMeasuredZeroRatherThanNull()
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.Add(NewsRiskTestData.Observation(
            Company, "Test Co flags doubt", AsOf.AddDays(-1), publisher: "Reuters"));

        var artifacts = new FakeArtifactStore();
        await Build(
                runStore, archive, new InMemoryAssessmentStore(), artifacts,
                Reader("a", "model-a", new ScriptedAnalyzer(ThesisChallengedOutcome)))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        var company = Assert.Single(artifacts.LiveDocument!.Companies);
        Assert.Equal(0, company.SyndicatedDuplicateCount);
        Assert.Equal(0, company.SyndicatedDistinctPublisherCount);
    }

    /// <summary>
    /// THE compatibility criterion (spec §4 item 6): changing ONLY syndication breadth leaves the surviving
    /// articles, the <c>BundleHash</c>, the assessment cache choice, the model request and completeness
    /// byte-identical, while the v4 current-run fields change. Syndication is enumeration provenance sitting
    /// BESIDE a possibly cached reader result — never a reason to call the model again.
    /// </summary>
    [Fact]
    public async Task ChangingOnlySyndicationBreadth_MovesOnlyTheV4Fields()
    {
        // The SAME surviving observation in both runs, so the supplied article set cannot differ.
        var survivor = NewsRiskTestData.Observation(
            Company, "Test Co wins contract - Reuters", AsOf.AddDays(-1), publisher: "Reuters");

        var plain = await RunAsync(survivor);
        var syndicated = await RunAsync(
            survivor,
            NewsRiskTestData.Observation(
                Company, "Test Co wins contract - Yahoo Finance", AsOf.AddDays(-2), publisher: "Yahoo Finance"),
            NewsRiskTestData.Observation(
                Company, "Test Co wins contract - MarketWatch", AsOf.AddDays(-3), publisher: "MarketWatch"));

        var plainCompany = Assert.Single(plain.Artifacts.LiveDocument!.Companies);
        var syndicatedCompany = Assert.Single(syndicated.Artifacts.LiveDocument!.Companies);

        // Identical supplied articles …
        Assert.Equal(
            plainCompany.Articles.Select(a => a.ObservationId),
            syndicatedCompany.Articles.Select(a => a.ObservationId));

        // … identical input-bundle hash, so the assessment CACHE KEY does not move …
        var plainRecord = Assert.Single(plain.Assessments.Records);
        var syndicatedRecord = Assert.Single(syndicated.Assessments.Records);
        Assert.Equal(plainRecord.InputBundleHash, syndicatedRecord.InputBundleHash);
        Assert.Equal(plainRecord.CohortKey, syndicatedRecord.CohortKey);

        // … identical completeness, and an identical MODEL REQUEST (the judge/reader never sees syndication).
        Assert.Equal(plainRecord.AssessmentBundle, syndicatedRecord.AssessmentBundle);
        Assert.Equal(plainCompany.QualifyingArticleCount, syndicatedCompany.QualifyingArticleCount);
        Assert.Equal(
            RequestShape(plain.Analyzer.Requests), RequestShape(syndicated.Analyzer.Requests));

        // … while ONLY the current-run enumeration provenance moves.
        Assert.Equal(0, plainCompany.SyndicatedDuplicateCount);
        Assert.Equal(2, syndicatedCompany.SyndicatedDuplicateCount);
        Assert.Equal(0, plainCompany.SyndicatedDistinctPublisherCount);
        Assert.Equal(3, syndicatedCompany.SyndicatedDistinctPublisherCount);
    }

    /// <summary>The model request's whole observable shape — asserted equal, not merely spot-checked.</summary>
    private static string RequestShape(IEnumerable<NewsRiskAnalysisRequest> requests) => string.Join(
        "\n",
        requests.Select(r => string.Join(
            "|",
            r.CompanyName,
            r.Ticker,
            string.Join(
                ";",
                r.Articles.Select(a => $"{a.Headline}/{a.DescriptionText}/{a.BodyText}")))));

    private sealed record SyndicationRun(
        FakeArtifactStore Artifacts, InMemoryAssessmentStore Assessments, ScriptedAnalyzer Analyzer);

    private static async Task<SyndicationRun> RunAsync(params NewsObservationRecord[] observations)
    {
        var runStore = new FakeRunStore();
        runStore.Records.Add(RunRecord(BatchId));
        var archive = new FakeArchive { Batch = CompleteBatch(Company) };
        archive.Observations.AddRange(observations);

        var assessments = new InMemoryAssessmentStore();
        var artifacts = new FakeArtifactStore();
        var analyzer = new ScriptedAnalyzer(ThesisChallengedOutcome);

        await Build(runStore, archive, assessments, artifacts, Reader("a", "model-a", analyzer))
            .GenerateAsync(RunId, Sections(Company), CancellationToken.None);

        return new SyndicationRun(artifacts, assessments, analyzer);
    }
}
