using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Tests.NewsRisk;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.News;

/// <summary>
/// Spec 191 §2/§3 — admission and mapping. A judgment contributes DIRECTION only from the prospectively
/// designated presentation cohort, only when it is <c>Judged</c> with a trajectory, and only when it was
/// created at or before the index's as-of instant; latest per company wins, ties on the lowest judgment id.
/// Everything else falls back to "Radar has not read this article" (a <c>null</c> read).
/// </summary>
public sealed class NewsDirectionalReadSourceTests
{
    private const string PresentationCohort = "presentation-cohort";

    private static readonly Guid CompanyA = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid CompanyB = new("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset Observed = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    private static Guid Id(int n) => new($"cccccccc-0000-0000-0000-{n:D12}");

    private static EvidenceItem News(Guid id, string title) => new(
        Id: id,
        SourceType: EvidenceSourceType.NewsArticle,
        SourceName: "Example Wire",
        SourceUrl: "https://example.com/a",
        Title: title,
        Summary: null,
        RawText: title + " — body",
        ContentHash: "hash-" + id.ToString("N"),
        PublishedAtUtc: Observed,
        CollectedAtUtc: Observed,
        Quality: EvidenceQuality.Medium,
        MetadataJson: null);

    private static NewsJudgmentRecord Judgment(
        Guid companyId,
        NewsJudgmentStatus status = NewsJudgmentStatus.Judged,
        NewsJudgmentTrajectory? trajectory = NewsJudgmentTrajectory.Improving,
        string cohortKey = PresentationCohort,
        DateTimeOffset? createdAtUtc = null,
        Guid? judgmentId = null,
        int findingCount = 0,
        NewsTypingCompleteness typingCompleteness = NewsTypingCompleteness.Backlog) => new(
        SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
        JudgmentId: judgmentId ?? Guid.NewGuid(),
        RunId: null,
        CompanyId: companyId,
        CompanyName: "Test Co",
        Ticker: "TST",
        JudgeName: "deepinfra-deepseek",
        Provider: "openai",
        ModelId: "deepseek-ai/DeepSeek-V4-Flash",
        PromptVersion: NewsJudgmentContract.PromptVersion,
        ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
        Stage1CohortKey: "stage1",
        TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
        TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
        FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
        CohortKey: cohortKey,
        FamilySetHash: "hash",
        Families: [],
        ArchiveCapture: NewsRiskArchiveCapture.Proven,
        SearchEnumeration: NewsRiskSearchEnumeration.Complete,
        ObservationSupply: NewsRiskAssessmentBundle.Complete,
        TypingCompleteness: typingCompleteness,
        FamilyBundle: NewsJudgmentFamilyBundle.Complete,
        CoverageIssues: [],
        Status: status,
        BusinessTrajectory: trajectory,
        ChallengeStrength: null,
        Findings: [.. Enumerable.Range(0, findingCount).Select(_ => new NewsJudgmentValidatedFinding(
            NewsRiskCategory.RegulatoryOrLegalSetback,
            NewsRiskSeverity.High,
            0.8,
            [Guid.NewGuid()],
            null))],
        Rationale: null,
        FindingsTotal: findingCount,
        FindingsAccepted: findingCount,
        FindingsDropped: 0,
        FindingDropReasons: [],
        RawResponseHash: null,
        FailureDetail: null,
        Limits: new NewsJudgmentLimitsRecord(30, 50),
        ReusedFromJudgmentId: null,
        CreatedAtUtc: createdAtUtc ?? Observed);

    private static async Task<NewsDirectionalReadSource> SourceAsync(
        IReadOnlyList<NewsObservationRecord> observations,
        IReadOnlyList<EvidenceItem> evidence,
        IReadOnlyList<NewsJudgmentRecord> judgments,
        DateTimeOffset? asOfUtc = null,
        string cohortKey = PresentationCohort)
    {
        var archive = new InMemoryNewsObservationArchive();
        foreach (var observation in observations)
        {
            await archive.WriteAsync(observation, CancellationToken.None);
        }

        var evidenceRepository = new InMemoryEvidenceRepository();
        foreach (var item in evidence)
        {
            await evidenceRepository.AddIfNewAsync(item, CancellationToken.None);
        }

        var source = new NewsDirectionalReadSource(
            archive,
            evidenceRepository,
            new FakeJudgmentStore(judgments),
            new NewsDirectionalReadOptions(cohortKey),
            NullLogger<NewsDirectionalReadSource>.Instance);

        // Every test below reads through a PREPARED source, at the RUN as-of instant CollectionPass supplies:
        // the point-in-time bound is the run instant, never a clock read of the source's own.
        await source.PrepareAsync(asOfUtc ?? AsOf, CancellationToken.None);
        return source;
    }

    [Theory]
    [InlineData(NewsJudgmentTrajectory.Improving, SignalDirection.Positive, "improving")]
    [InlineData(NewsJudgmentTrajectory.Deteriorating, SignalDirection.Negative, "deteriorating")]
    public async Task JudgedTrajectory_MapsToItsDeclaredDirection_AndCarriesTheProvenanceTriple(
        NewsJudgmentTrajectory trajectory, SignalDirection direction, string token)
    {
        var evidenceId = Id(1);
        var observationId = Id(101);
        var judgmentId = Id(201);
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed, observationId: observationId)],
            [News(evidenceId, "Acme wins order")],
            [Judgment(CompanyA, trajectory: trajectory, judgmentId: judgmentId)]);

        var read = await source.TryReadAsync(News(evidenceId, "Acme wins order"), CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(direction, read.Direction);
        Assert.Equal(observationId, read.ObservationId);
        Assert.Equal(judgmentId, read.JudgmentId);
        Assert.Equal(PresentationCohort, read.JudgmentCohortKey);
        Assert.Equal(token, read.TrajectoryToken);
    }

    [Theory]
    [InlineData(NewsJudgmentTrajectory.Mixed)]
    [InlineData(NewsJudgmentTrajectory.Unknown)]
    public async Task MixedAndUnknown_AreNoDirection_NotAWeakPositive(NewsJudgmentTrajectory trajectory)
    {
        var evidenceId = Id(2);
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(evidenceId, "Acme wins order")],
            [Judgment(CompanyA, trajectory: trajectory)]);

        Assert.Null(await source.TryReadAsync(News(evidenceId, "Acme wins order"), CancellationToken.None));
    }

    [Theory]
    [InlineData(NewsJudgmentStatus.ValidationFailed)]
    [InlineData(NewsJudgmentStatus.InsufficientFacts)]
    [InlineData(NewsJudgmentStatus.ProviderFailure)]
    [InlineData(NewsJudgmentStatus.ParseFailure)]
    [InlineData(NewsJudgmentStatus.AttemptsExhausted)]
    public async Task EveryNonJudgedStatus_ContributesNoDirection(NewsJudgmentStatus status)
    {
        var evidenceId = Id(3);
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(evidenceId, "Acme wins order")],
            // Deliberately WITH a trajectory: only the STATUS may disqualify it here.
            [Judgment(CompanyA, status: status, trajectory: NewsJudgmentTrajectory.Improving)]);

        Assert.Null(await source.TryReadAsync(News(evidenceId, "Acme wins order"), CancellationToken.None));
    }

    [Fact]
    public async Task JudgedWithNullTrajectory_ContributesNoDirection()
    {
        var evidenceId = Id(4);
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(evidenceId, "Acme wins order")],
            [Judgment(CompanyA, trajectory: null)]);

        Assert.Null(await source.TryReadAsync(News(evidenceId, "Acme wins order"), CancellationToken.None));
    }

    [Fact]
    public async Task AJudgmentFromANonPresentationCohort_ContributesNothing()
    {
        var evidenceId = Id(5);
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(evidenceId, "Acme wins order")],
            [Judgment(CompanyA, cohortKey: "some-other-cohort")]);

        Assert.Null(await source.TryReadAsync(News(evidenceId, "Acme wins order"), CancellationToken.None));
    }

    [Fact]
    public async Task AJudgmentCreatedAfterTheAsOfInstant_IsInvisible()
    {
        // Spec 136's point-in-time predicate: CreatedAtUtc <= asOfUtc. The bound is INCLUSIVE, so a judgment
        // created exactly at the instant IS visible.
        var evidenceId = Id(6);
        var observations = new[]
        {
            NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed),
        };
        var evidence = new[] { News(evidenceId, "Acme wins order") };

        var future = await SourceAsync(
            observations, evidence, [Judgment(CompanyA, createdAtUtc: AsOf.AddTicks(1))]);
        var exactlyNow = await SourceAsync(
            observations, evidence, [Judgment(CompanyA, createdAtUtc: AsOf)]);

        Assert.Null(await future.TryReadAsync(evidence[0], CancellationToken.None));
        Assert.NotNull(await exactlyNow.TryReadAsync(evidence[0], CancellationToken.None));
    }

    [Fact]
    public async Task LatestAdmittedJudgmentPerCompanyWins_TieBreaksOnTheLowestJudgmentId()
    {
        var evidenceId = Id(7);
        var evidence = News(evidenceId, "Acme wins order");
        var observations = new[]
        {
            NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed),
        };

        var latest = await SourceAsync(
            observations,
            [evidence],
            [
                Judgment(
                    CompanyA,
                    trajectory: NewsJudgmentTrajectory.Deteriorating,
                    createdAtUtc: Observed,
                    judgmentId: Id(210)),
                Judgment(
                    CompanyA,
                    trajectory: NewsJudgmentTrajectory.Improving,
                    createdAtUtc: Observed.AddHours(1),
                    judgmentId: Id(220)),
            ]);
        Assert.Equal(SignalDirection.Positive, (await latest.TryReadAsync(evidence, CancellationToken.None))!.Direction);

        var tied = await SourceAsync(
            observations,
            [evidence],
            [
                Judgment(
                    CompanyA,
                    trajectory: NewsJudgmentTrajectory.Improving,
                    createdAtUtc: Observed,
                    judgmentId: Id(230)),
                Judgment(
                    CompanyA,
                    trajectory: NewsJudgmentTrajectory.Deteriorating,
                    createdAtUtc: Observed,
                    judgmentId: Id(220)),
            ]);
        var tieRead = await tied.TryReadAsync(evidence, CancellationToken.None);
        Assert.Equal(Id(220), tieRead!.JudgmentId);
        Assert.Equal(SignalDirection.Negative, tieRead.Direction);
    }

    [Fact]
    public async Task UnjoinedEvidence_ContributesNoDirection_EvenWithAnAdmittedJudgment()
    {
        var evidenceId = Id(8);
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "A different headline entirely", Observed)],
            [News(evidenceId, "Acme wins order")],
            [Judgment(CompanyA)]);

        Assert.Null(await source.TryReadAsync(News(evidenceId, "Acme wins order"), CancellationToken.None));
    }

    [Fact]
    public async Task AnAmbiguousJoin_ContributesNoDirection()
    {
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(Id(9), "Acme wins order"), News(Id(10), "Acme wins order")],
            [Judgment(CompanyA)]);

        Assert.Null(await source.TryReadAsync(News(Id(9), "Acme wins order"), CancellationToken.None));
        Assert.Null(await source.TryReadAsync(News(Id(10), "Acme wins order"), CancellationToken.None));
    }

    [Fact]
    public async Task ASameHeadlineArticleOfADifferentCompany_NeverInheritsTheOthersDirection()
    {
        var evidenceId = Id(11);
        var source = await SourceAsync(
            [
                NewsRiskTestData.Observation(CompanyA, "Sector index rises", Observed),
                NewsRiskTestData.Observation(CompanyB, "Sector index rises", Observed),
            ],
            [News(evidenceId, "Sector index rises")],
            [Judgment(CompanyA), Judgment(CompanyB, trajectory: NewsJudgmentTrajectory.Deteriorating)]);

        Assert.Null(await source.TryReadAsync(News(evidenceId, "Sector index rises"), CancellationToken.None));
    }

    [Fact]
    public async Task NonNewsEvidence_IsNeverRead()
    {
        var source = await SourceAsync([], [], []);
        var filing = News(Id(12), "Acme wins order") with { SourceType = EvidenceSourceType.Filing };

        Assert.Null(await source.TryReadAsync(filing, CancellationToken.None));
    }

    [Theory]
    // A supportive Improving read legitimately carries ZERO findings (spec 185 findings are challenge-only)
    // and therefore lands at the base strength — today's Neutral strength — unless typing was complete.
    [InlineData(0, NewsTypingCompleteness.Backlog, 4)]
    [InlineData(0, NewsTypingCompleteness.Complete, 5)]
    [InlineData(1, NewsTypingCompleteness.Backlog, 5)]
    [InlineData(3, NewsTypingCompleteness.Backlog, 7)]
    [InlineData(3, NewsTypingCompleteness.Complete, 8)]
    // The finding contribution is capped at 3, so strength never leaves 4..8.
    [InlineData(50, NewsTypingCompleteness.Complete, 8)]
    public async Task Strength_ScalesByFindingCountAndTypingCompleteness_WithinTheDomainRange(
        int findingCount, NewsTypingCompleteness completeness, int expected)
    {
        var evidenceId = Id(13);
        var source = await SourceAsync(
            [NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed)],
            [News(evidenceId, "Acme wins order")],
            [Judgment(CompanyA, findingCount: findingCount, typingCompleteness: completeness)]);

        var read = await source.TryReadAsync(News(evidenceId, "Acme wins order"), CancellationToken.None);

        Assert.Equal(expected, read!.Strength);
        Assert.InRange(read.Strength, 1, 10);
    }

    [Fact]
    public async Task AnUnpreparedSource_FailsClosed_AndNeverBuildsImplicitly()
    {
        // TryReadAsync before any PrepareAsync returns null and must NOT touch a store: an implicit build
        // would invent an as-of instant of its own, which is exactly the hindsight the run-scoped bound
        // exists to prevent.
        var (archive, evidenceRepository, evidence, store) = await FixtureAsync();
        store.Records = [Judgment(CompanyA)];
        var source = Source(archive, evidenceRepository, store);

        Assert.Null(await source.TryReadAsync(evidence, CancellationToken.None));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task RePreparingAtTheSameInstant_IsANoOp_SoOneRunHydratesOnce()
    {
        var (archive, evidenceRepository, evidence, store) = await FixtureAsync();
        store.Records = [Judgment(CompanyA)];
        var source = Source(archive, evidenceRepository, store);

        await source.PrepareAsync(AsOf, CancellationToken.None);
        await source.PrepareAsync(AsOf, CancellationToken.None);
        await source.PrepareAsync(AsOf, CancellationToken.None);

        Assert.NotNull(await source.TryReadAsync(evidence, CancellationToken.None));
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task PreparingAtANewInstant_Rebuilds_SoASecondRunInOneProcessSeesNewJudgments()
    {
        // THE STALENESS FIX. With Radar:RunOnce=false the Worker runs the pipeline repeatedly in ONE
        // process, and each run's post-pipeline judgment step writes new judgments. A once-per-instance
        // index would freeze the news read at run 1 forever. This test fails if PrepareAsync ignores a
        // changed asOfUtc.
        var (archive, evidenceRepository, evidence, store) = await FixtureAsync();
        var source = Source(archive, evidenceRepository, store);

        // Run 1: no judgment yet, so the Neutral fallback stands.
        await source.PrepareAsync(AsOf, CancellationToken.None);
        Assert.Null(await source.TryReadAsync(evidence, CancellationToken.None));

        // Run 1's post-pipeline judgment step writes a verdict.
        store.Records = [Judgment(CompanyA, createdAtUtc: AsOf.AddHours(1))];

        // Run 2, at ITS OWN as-of instant: the verdict is now visible.
        await source.PrepareAsync(AsOf.AddHours(24), CancellationToken.None);
        var read = await source.TryReadAsync(evidence, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(SignalDirection.Positive, read.Direction);
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task AJudgmentWithNoRecordableJudgmentId_IsNeverEmittedDirectionally()
    {
        // Spec 191 §2 mandatory provenance, defence in depth: a record whose judgment id cannot be recorded
        // contributes NO direction rather than an untraceable one.
        var (archive, evidenceRepository, evidence, store) = await FixtureAsync();
        store.Records = [Judgment(CompanyA, judgmentId: Guid.Empty)];
        var source = Source(archive, evidenceRepository, store);
        await source.PrepareAsync(AsOf, CancellationToken.None);

        Assert.Null(await source.TryReadAsync(evidence, CancellationToken.None));
    }

    [Fact]
    public async Task AWhitespaceCohortKey_IsNeitherAdmittedNorEmittedDirectionally()
    {
        // Two independent refusals, both ending at the Neutral fallback: the cohort key is matched
        // case-SENSITIVELY against the configured key (so a whitespace key is not admitted at all), and even
        // when it IS the configured key the provenance guard refuses to mint a signal whose cohort key
        // cannot be recorded.
        var (archive, evidenceRepository, evidence, store) = await FixtureAsync();
        store.Records = [Judgment(CompanyA, cohortKey: "   ")];

        var configuredNormally = Source(archive, evidenceRepository, store);
        await configuredNormally.PrepareAsync(AsOf, CancellationToken.None);
        Assert.Null(await configuredNormally.TryReadAsync(evidence, CancellationToken.None));

        var configuredWithTheBlankKey = Source(archive, evidenceRepository, store, cohortKey: "   ");
        await configuredWithTheBlankKey.PrepareAsync(AsOf, CancellationToken.None);
        Assert.Null(await configuredWithTheBlankKey.TryReadAsync(evidence, CancellationToken.None));
    }

    /// <summary>One archived observation + its matching news evidence + an initially empty judgment store.</summary>
    private static async Task<(InMemoryNewsObservationArchive Archive, InMemoryEvidenceRepository Evidence,
        EvidenceItem Item, FakeJudgmentStore Store)> FixtureAsync()
    {
        var archive = new InMemoryNewsObservationArchive();
        await archive.WriteAsync(
            NewsRiskTestData.Observation(CompanyA, "Acme wins order", Observed), CancellationToken.None);
        var evidenceRepository = new InMemoryEvidenceRepository();
        var evidence = News(Id(14), "Acme wins order");
        await evidenceRepository.AddIfNewAsync(evidence, CancellationToken.None);
        return (archive, evidenceRepository, evidence, new FakeJudgmentStore([]));
    }

    private static NewsDirectionalReadSource Source(
        InMemoryNewsObservationArchive archive,
        InMemoryEvidenceRepository evidence,
        FakeJudgmentStore store,
        string cohortKey = PresentationCohort) => new(
        archive,
        evidence,
        store,
        new NewsDirectionalReadOptions(cohortKey),
        NullLogger<NewsDirectionalReadSource>.Instance);

    private sealed class FakeJudgmentStore(IReadOnlyList<NewsJudgmentRecord> records) : INewsJudgmentStore
    {
        public IReadOnlyList<NewsJudgmentRecord> Records { get; set; } = records;

        public int Reads { get; private set; }

        public Task<bool> WriteAsync(NewsJudgmentRecord record, CancellationToken ct) =>
            throw new NotSupportedException("The directional read source is read-only.");

        public Task<IReadOnlyList<NewsJudgmentRecord>> GetAllAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Records);
        }

        public Task<NewsJudgmentRecord?> FindCompletedAsync(
            string cohortKey, Guid companyId, string familySetHash, CancellationToken ct) =>
            throw new NotSupportedException("The directional read source never consults the cache.");
    }
}
