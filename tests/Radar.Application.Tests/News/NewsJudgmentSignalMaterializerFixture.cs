using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Application.Storage;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.News;

/// <summary>
/// Deterministic, CONSTRUCTED fixtures for the spec-194 §1.2 judgment-signal materializer tests. Nothing
/// here reads live data: every observation, evidence item, fact and judgment is built in the test, so no
/// assertion can go green because of what happens to be on disk.
/// </summary>
internal static class MaterializerFixture
{
    public const string JudgeName = "ambient";
    public const string ExtractorName = "ambient";

    public static readonly DateTimeOffset Monday = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Tuesday = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    public static NewsJudgmentOptions Options(
        string presentationJudge = JudgeName, string presentationExtractor = ExtractorName) => new(
        outputDirectory: Path.Combine(Path.GetTempPath(), "radar-materializer-tests"),
        maxCompaniesPerRun: 30,
        maxFamiliesPerJudgment: 50,
        maxJudgmentAttempts: 3,
        presentationJudge: presentationJudge,
        presentationExtractor: presentationExtractor,
        newsSearchCollectorName: "newssearch");

    public static NewsJudgmentReaderSet Judges(string name = JudgeName) => new(
    [
        new NewsJudgmentReader(
            new NewsJudgmentReaderIdentity(name, "openai", "test-judge"), new NeverCalledAnalyzer()),
    ]);

    public static NewsTypingReaderIdentity Extractor(string name = ExtractorName) =>
        new(name, "openai", "test-extractor");

    public static NewsTypingRunResult Typing(
        IReadOnlyDictionary<Guid, NewsTypingFactRef> factsById,
        string extractorName = ExtractorName) => new(
        RunId: null,
        WindowStartUtc: Monday.AddDays(-30),
        WindowEndUtc: Now,
        NewsObservationBatchId: null,
        Cohorts:
        [
            new NewsTypingCohortRunResult(
                Reader: Extractor(extractorName),
                Families: [],
                FactsById: factsById,
                TypingCompletenessByCompany: new Dictionary<Guid, NewsTypingCompleteness>(),
                FactsDroppedInWindow: 0,
                RetryExhausted: 0),
        ]);

    /// <summary>The stage-2 cohort key the designated (judge, extractor) pair composes — the ONLY key an eligible record may carry.</summary>
    public static string PresentationCohortKey(
        string judgeName = JudgeName, string extractorName = ExtractorName) =>
        Judges(judgeName).Readers[0].Identity.CohortKeyFor(Extractor(extractorName).CohortKey);

    public static EvidenceItem NewsEvidence(
        string title, string body, DateTimeOffset publishedAtUtc, Guid? id = null) => new(
        Id: id ?? Guid.NewGuid(),
        SourceType: EvidenceSourceType.NewsArticle,
        SourceName: "Example Wire",
        SourceUrl: "https://example.test/" + Guid.NewGuid().ToString("N"),
        Title: title,
        Summary: null,
        RawText: body,
        ContentHash: Guid.NewGuid().ToString("N"),
        PublishedAtUtc: publishedAtUtc,
        CollectedAtUtc: publishedAtUtc,
        // Medium (not Unknown/Low): the deterministic reviewer treats a weak source as a
        // confidence-reduction trigger, and these tests are about materialization, not about review rules.
        Quality: EvidenceQuality.Medium,
        MetadataJson: null);

    public static NewsObservationRecord Observation(
        Guid companyId, string headline, DateTimeOffset at, Guid? observationId = null) => new(
        SchemaVersion: NewsObservationRecord.CurrentSchemaVersion,
        ObservationId: observationId ?? Guid.NewGuid(),
        CompanyId: companyId,
        Ticker: "ACM",
        Collector: "newssearch",
        QueryPhrase: null,
        FeedId: null,
        FeedName: null,
        GoogleLandingUrl: "https://news.example.test/" + Guid.NewGuid().ToString("N"),
        Publisher: "Example Wire",
        PublisherSiteUrl: null,
        Headline: headline,
        DescriptionRaw: null,
        DescriptionText: null,
        DescriptionTruncated: false,
        PublishedAtUtc: at,
        RetrievedAtUtc: at,
        FirstObservedAtUtc: at,
        PayloadHash: Guid.NewGuid().ToString("N"),
        CaptureMode: NewsObservationCaptureMode.ProspectiveRss,
        ArticleFetch: null);

    public static NewsTypingFactRef FactRef(
        Guid factId, Guid? companyId, Guid observationId, string statement, params string[] citations) =>
        new(
            Fact: new NewsTypingValidatedFact(
                FactId: factId,
                EventTypes: [NewsEventType.EarningsOrGuidance],
                Statement: statement,
                TemporalScope: "Q2 2026",
                Attribution: NewsFactAttribution.Publisher,
                AssertionStatus: NewsFactAssertionStatus.Reported,
                Confidence: 0.9,
                Citations: citations),
            ObservationId: observationId,
            CompanyId: companyId,
            CaptureMode: NewsObservationCaptureMode.ProspectiveRss);

    public static NewsJudgmentRecord Judgment(
        Guid companyId,
        string cohortKey,
        NewsJudgmentTrajectory? trajectory,
        IReadOnlyList<Guid>? trajectoryFactIds,
        NewsJudgmentStatus status = NewsJudgmentStatus.Judged,
        Guid? judgmentId = null,
        int findings = 0,
        NewsTypingCompleteness typingCompleteness = NewsTypingCompleteness.Backlog,
        string companyName = "Acme Corporation",
        DateTimeOffset? createdAtUtc = null) => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: judgmentId ?? Guid.NewGuid(),
        RunId: null,
        CompanyId: companyId,
        CompanyName: companyName,
        Ticker: "ACM",
        JudgeName: JudgeName,
        Provider: "openai",
        ModelId: "test-judge",
        PromptVersion: NewsJudgmentContract.PromptVersion,
        ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
        Stage1CohortKey: Extractor().CohortKey,
        TaxonomyVersion: NewsEventTaxonomy.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
        CohortKey: cohortKey,
        FamilySetHash: "fs-1",
        Families: [],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        ObservationSupply: NewsRiskAssessmentBundle.Complete,
        TypingCompleteness: typingCompleteness,
        FamilyBundle: NewsJudgmentFamilyBundle.Complete,
        CoverageIssues: [],
        Status: status,
        BusinessTrajectory: trajectory,
        ChallengeStrength: 60,
        Findings: BuildFindings(findings),
        Rationale: "The cited filing shows revenue decline.",
        FindingsTotal: findings,
        FindingsAccepted: findings,
        FindingsDropped: 0,
        FindingDropReasons: [],
        RawResponseHash: "rh-1",
        FailureDetail: null,
        Limits: new NewsJudgmentLimitsRecord(30, 50, 3),
        ReusedFromJudgmentId: null,
        CreatedAtUtc: createdAtUtc ?? Monday,
        TrajectoryFactIds: trajectoryFactIds);

    private static IReadOnlyList<NewsJudgmentValidatedFinding> BuildFindings(int count) =>
    [
        .. Enumerable.Range(0, count).Select(_ => new NewsJudgmentValidatedFinding(
            Category: NewsRiskCategory.RegulatoryOrLegalSetback,
            Severity: NewsRiskSeverity.Medium,
            Confidence: 0.8,
            FactIds: [Guid.NewGuid()],
            AttributionCaveat: null)),
    ];

    public static NewsJudgmentRunResult RunResult(params NewsJudgmentRecord[] records) =>
        new(records, Markers: null, Stage1FactsDroppedByCohort: new Dictionary<string, int>());

    /// <summary>An analyzer that fails the test if the materializer ever reaches a provider (it must make NO model call).</summary>
    private sealed class NeverCalledAnalyzer : INewsJudgmentAnalyzer
    {
        public Task<NewsJudgmentAnalysisOutcome> AnalyzeAsync(
            NewsJudgmentAnalysisRequest request, CancellationToken ct) =>
            throw new InvalidOperationException(
                "The judgment-signal materializer must make no model call.");
    }
}

/// <summary>An in-memory observation archive: the materializer only ever reads it.</summary>
internal sealed class FakeObservationArchive(params NewsObservationRecord[] records)
    : INewsObservationArchive
{
    public Task<NewsObservationWriteOutcome> WriteAsync(
        NewsObservationRecord record, CancellationToken ct) =>
        throw new NotSupportedException("The materializer never writes observations.");

    public Task<bool> WriteBatchAsync(NewsObservationBatch batch, CancellationToken ct) =>
        throw new NotSupportedException("The materializer never writes batches.");

    public Task<IReadOnlyList<NewsObservationRecord>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NewsObservationRecord>>(records);
}

/// <summary>
/// A signal file store that RECORDS what it was asked to persist and can be told to fail, so a test can
/// distinguish "materialized" from spec 193's "counted, not reported as materialized".
/// </summary>
internal sealed class RecordingSignalFileStore : ISignalFileStore
{
    private readonly List<(Signal Signal, Radar.Domain.Signals.SignalReview Review)> _writes = [];

    public IReadOnlyList<(Signal Signal, Radar.Domain.Signals.SignalReview Review)> Writes => _writes;

    public bool FailWrites { get; set; }

    public Task<DurableWriteResult> WriteAsync(
        Signal signal, Radar.Domain.Signals.SignalReview review, CancellationToken ct)
    {
        // The production store's provenance guard, mirrored: a review that does not belong to the signal is
        // a contract violation, not a soft failure. Keeping it here is what makes "the id is set BEFORE
        // review" a tested property rather than an assertion in a comment.
        if (review.SignalId != signal.Id)
        {
            throw new ArgumentException("Review does not belong to the signal.", nameof(review));
        }

        _writes.Add((signal, review));
        return Task.FromResult(FailWrites
            ? DurableWriteResult.NotPersisted("(test)")
            : DurableWriteResult.Succeeded("(test)"));
    }

    public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
        Guid companyId,
        DateTimeOffset startExclusiveUtc,
        DateTimeOffset endInclusiveUtc,
        DateTimeOffset knownAsOfUtc,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Signal>>([]);
}

/// <summary>A fixed clock — the materialization instant every test pins <c>CreatedAtUtc</c> against.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Wraps the REAL deterministic reviewer and counts invocations — test 4 asserts it is not called a second time.</summary>
internal sealed class CountingReviewer(TimeProvider timeProvider) : ISignalReviewer
{
    private readonly DeterministicSignalReviewer _inner =
        new(timeProvider, NullLogger<DeterministicSignalReviewer>.Instance);

    public int Calls { get; private set; }

    public Task<SignalReviewOutcome> ReviewAsync(
        Signal signal, EvidenceItem evidence, CancellationToken ct)
    {
        Calls++;
        return _inner.ReviewAsync(signal, evidence, ct);
    }
}

/// <summary>An evidence repository whose <c>GetAllAsync</c> throws — proof that the store reads are skipped when nothing is eligible.</summary>
internal sealed class ThrowingEvidenceRepository : IEvidenceRepository
{
    public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct) =>
        throw new InvalidOperationException(
            "The evidence store must not be read when no judgment survived the cheap gates.");
}

/// <summary>A signal repository whose by-id read throws for ONE company's signal id — the "one unexpected failure" fixture.</summary>
internal sealed class ExplodingSignalRepository(Guid explodingSignalId) : ISignalRepository
{
    private readonly InMemorySignalRepository _inner = new();

    public Task AddAsync(Signal signal, CancellationToken ct) => _inner.AddAsync(signal, ct);

    public Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct) => id == explodingSignalId
        ? throw new InvalidOperationException("Simulated store failure for one company.")
        : _inner.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct) =>
        _inner.GetByCompanyAsync(companyId, ct);

    public Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
        _inner.GetObservedBetweenAsync(startUtc, endUtc, ct);
}
