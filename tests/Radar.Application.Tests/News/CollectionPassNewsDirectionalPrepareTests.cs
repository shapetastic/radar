using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Evidence;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Pipeline;
using Radar.Application.SignalExtraction;
using Radar.Application.SignalReview;
using Radar.Application.Signals;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.News;

/// <summary>
/// Spec 191 at the ORCHESTRATION seam: <see cref="CollectionPass"/> prepares the directional news read ONCE
/// per run, at the run's own captured <c>asOfUtc</c> — the same instant it stamps as every signal's
/// <c>CreatedAtUtc</c> — mirroring the directional-filing <c>ProduceAsync(candidates, asOfUtc, ct)</c> call
/// beside it.
/// <para>
/// The regression this closes: the source is a container SINGLETON, and with <c>Radar:RunOnce=false</c> the
/// Worker runs the pipeline repeatedly in ONE process while each run's post-pipeline judgment step writes new
/// judgments. A once-per-instance index would have frozen the news read at run 1 forever. These tests drive
/// the REAL <see cref="NewsDirectionalReadSource"/> through two real passes and assert the second run sees
/// the first run's verdict.
/// </para>
/// </summary>
public sealed class CollectionPassNewsDirectionalPrepareTests
{
    private static readonly Guid CompanyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string HeadlineOne = "Rocket Lab wins new launch contract - SpaceNews";
    private const string HeadlineTwo = "Rocket Lab opens a second production line - SpaceNews";
    private const string PresentationCohort = "presentation-cohort";

    private static string UrlFor(string headline) =>
        "https://news.google.com/rss/articles/" + headline.GetHashCode(StringComparison.Ordinal).ToString("x8");

    private static NewsObservationCandidate Candidate(string headline, DateTimeOffset retrievedAtUtc) => new(
        CompanyId: CompanyId,
        Ticker: "RKLB",
        Collector: "newssearch",
        QueryPhrase: "Rocket Lab",
        FeedId: Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        FeedName: "Rocket Lab — News",
        GoogleLandingUrl: UrlFor(headline),
        Publisher: "SpaceNews",
        PublisherSiteUrl: "https://spacenews.com",
        Headline: headline,
        DescriptionRaw: null,
        DescriptionText: null,
        DescriptionTruncated: false,
        PublishedAtUtc: retrievedAtUtc.AddHours(-3),
        RetrievedAtUtc: retrievedAtUtc);

    private static CollectedEvidence Evidence(string headline, DateTimeOffset collectedAtUtc) => new(
        SourceType: EvidenceSourceType.NewsArticle,
        SourceName: "SpaceNews",
        SourceUrl: UrlFor(headline),
        Title: headline,
        RawText: $"{headline} — SpaceNews. Source: {UrlFor(headline)}",
        PublishedAt: collectedAtUtc.AddHours(-3),
        CollectedAt: collectedAtUtc,
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    [Fact]
    public async Task ASecondRunInTheSameProcess_SeesJudgmentsTheFirstRunProduced()
    {
        var run1 = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var run2 = run1.AddDays(1);

        // ONE archive, ONE evidence repository, ONE judgment store and ONE read source — the singleton
        // lifetimes a long-running Worker really has.
        var archive = new InMemoryNewsObservationArchive();
        var evidenceRepository = new InMemoryEvidenceRepository();
        var judgments = new MutableJudgmentStore();
        var readSource = new NewsDirectionalReadSource(
            archive,
            evidenceRepository,
            judgments,
            new NewsDirectionalReadOptions(PresentationCohort),
            NullLogger<NewsDirectionalReadSource>.Instance);
        var signals = new RecordingSignalStore();

        // ---- RUN 1: collects article ONE; no judgment exists yet, so the signal is Neutral.
        var pass1 = CreatePass(run1, HeadlineOne, archive, evidenceRepository, readSource, signals);
        await pass1.RunAsync(CancellationToken.None);

        var first = Assert.Single(signals.Written);
        Assert.Equal(SignalType.MediaAttention, first.Type);
        Assert.Equal(SignalDirection.Neutral, first.Direction);
        Assert.Null(first.MetadataJson);

        // ---- Between runs, the post-pipeline judgment step persists a verdict for this company.
        judgments.Records = [Judgment(run1.AddHours(1))];

        // ---- RUN 2: the SAME process, the SAME singletons, a NEW as-of instant, and a genuinely NEW
        // article. (A re-collection of article ONE would be deduped by AddIfNewAsync and produce no signal
        // at all, and a same-headline second article would be an AMBIGUOUS join — both fail-closed by
        // design.) Article TWO joins to the observation this very run archived, and the run-1 verdict is now
        // visible ONLY because PrepareAsync rebuilt at run 2's instant.
        var pass2 = CreatePass(run2, HeadlineTwo, archive, evidenceRepository, readSource, signals);
        await pass2.RunAsync(CancellationToken.None);

        var second = Assert.Single(signals.Written, s => s.Id != first.Id);
        Assert.Equal(SignalType.MediaAttention, second.Type);
        Assert.Equal(SignalDirection.Positive, second.Direction);
        Assert.NotNull(second.MetadataJson);
    }

    [Fact]
    public async Task APassWithNoRegisteredReadSource_IsUnchanged()
    {
        // The optional trailing dependency: a composition without the spec-185 judgment step never prepares
        // and never reads, so the NewsArticle branch emits exactly the pre-191 Neutral signal.
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var signals = new RecordingSignalStore();

        var pass = CreatePass(
            now,
            HeadlineOne,
            new InMemoryNewsObservationArchive(),
            new InMemoryEvidenceRepository(),
            readSource: null,
            signals);
        await pass.RunAsync(CancellationToken.None);

        var signal = Assert.Single(signals.Written);
        Assert.Equal(SignalDirection.Neutral, signal.Direction);
        Assert.Null(signal.MetadataJson);
    }

    private static CollectionPass CreatePass(
        DateTimeOffset now,
        string headline,
        InMemoryNewsObservationArchive archive,
        InMemoryEvidenceRepository evidence,
        INewsDirectionalReadSource? readSource,
        RecordingSignalStore signalStore)
    {
        var companies = new InMemoryCompanyRepository();
        var at = now.AddMinutes(-5);
        var collector = new FakeCollector(new CollectionResult(
            [Evidence(headline, at)], CollectionSummary.Empty, null, [Candidate(headline, at)]));

        return new CollectionPass(
            [collector],
            new CollectedEvidenceMapper(new EvidenceNormalizer(), NullLogger<CollectedEvidenceMapper>.Instance),
            evidence,
            new NullRawStore(),
            new KeywordSignalExtractor(
                NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights(), readSource),
            new CompanyResolver(companies, NullLogger<CompanyResolver>.Instance),
            new DeterministicSignalReviewer(
                new FixedTime(now), NullLogger<DeterministicSignalReviewer>.Instance),
            new InMemorySignalRepository(),
            new InMemorySignalReviewRepository(),
            signalStore,
            companies,
            new CleanHealthValidator(),
            new FixedTime(now),
            NullLogger<CollectionPass>.Instance,
            directionalFilingSignals: null,
            newsObservationArchive: archive,
            newsObservationCaptureOptions: null,
            newsDirectionalReads: readSource);
    }

    private static NewsJudgmentRecord Judgment(DateTimeOffset createdAtUtc) => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        RunId: null,
        CompanyId: CompanyId,
        CompanyName: "Rocket Lab",
        Ticker: "RKLB",
        JudgeName: "deepinfra-deepseek",
        Provider: "openai",
        ModelId: "deepseek-ai/DeepSeek-V4-Flash",
        PromptVersion: NewsJudgmentContract.PromptVersion,
        ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
        Stage1CohortKey: "stage1",
        TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
        CohortKey: PresentationCohort,
        FamilySetHash: "hash",
        Families: [],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        ObservationSupply: NewsRiskAssessmentBundle.Complete,
        TypingCompleteness: NewsTypingCompleteness.Complete,
        FamilyBundle: NewsJudgmentFamilyBundle.Complete,
        CoverageIssues: [],
        Status: NewsJudgmentStatus.Judged,
        BusinessTrajectory: NewsJudgmentTrajectory.Improving,
        ChallengeStrength: null,
        Findings: [],
        Rationale: "Order intake rose against the prior quarter.",
        FindingsTotal: 0,
        FindingsAccepted: 0,
        FindingsDropped: 0,
        FindingDropReasons: [],
        RawResponseHash: null,
        FailureDetail: null,
        Limits: new NewsJudgmentLimitsRecord(30, 50),
        ReusedFromJudgmentId: null,
        CreatedAtUtc: createdAtUtc);

    private sealed class MutableJudgmentStore : INewsJudgmentStore
    {
        public IReadOnlyList<NewsJudgmentRecord> Records { get; set; } = [];

        public Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult(Records);

        public Task<NewsJudgmentRecord?> FindCompletedAsync(
            string cohortKey, Guid companyId, string familySetHash, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSignalStore : ISignalFileStore
    {
        public List<Signal> Written { get; } = [];

        public Task<string> WriteAsync(
            Signal signal, Domain.Signals.SignalReview review, CancellationToken ct)
        {
            Written.Add(signal);
            return Task.FromResult("(memory)");
        }

        public Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
            Guid companyId,
            DateTimeOffset startExclusiveUtc,
            DateTimeOffset endInclusiveUtc,
            DateTimeOffset knownAsOfUtc,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Signal>>([]);
    }

    private sealed class FakeCollector(CollectionResult result) : IEvidenceCollector
    {
        public string CollectorName => "newssearch";

        public EvidenceSourceType SourceType => EvidenceSourceType.NewsArticle;

        public Task<CollectionResult> CollectAsync(CollectionContext context, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class NullRawStore : IRawEvidenceStore
    {
        public Task<bool> WriteIfNewAsync(EvidenceItem evidence, CancellationToken ct) =>
            Task.FromResult(true);
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
}
