using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Tests.NewsRisk;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// Spec 187 §1's "Bound failed judgment attempts": the stricter <c>news-judgment-v2</c> validator makes a
/// persistent <see cref="NewsJudgmentStatus.ValidationFailed"/> likelier, so the same failed judgment must
/// not call the provider every night forever.
/// <para>
/// Every assertion here is on the ANALYZER'S CALL COUNT — the thing the bound actually protects — not on
/// how many records happen to be on disk (the spec-186 §2 typing precedent, for the same reason: a store
/// that deduplicates by identity can silently absorb a real hosted call).
/// </para>
/// </summary>
public sealed class NewsJudgmentAttemptBoundTests
{
    private static readonly Guid Eose = Guid.Parse("e05ee05e-e05e-e05e-e05e-e05ee05ee05e");

    // The store / batch-reader / counting-analyzer fakes live in NewsJudgmentPassFakes.cs: spec 187 §7's
    // provider-timing suite needs the SAME three, and a second copy of an insert-only store whose dedupe
    // behaviour is the whole point would be free to drift away from this one.

    /// <summary>A response that always fails v2 validation: a directional read citing no trajectory evidence.</summary>
    private static readonly Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> AlwaysInvalid =
        _ => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "Something looks worse.",
                TrajectoryFactIds: []),
            "raw-hash",
            null);

    private static NewsJudgmentOptions Options(
        int maxJudgmentAttempts = 3, string judgeName = "deepinfra-deepseek") => new(
        outputDirectory: "unused",
        maxCompaniesPerRun: 30,
        maxFamiliesPerJudgment: 50,
        maxJudgmentAttempts: maxJudgmentAttempts,
        presentationJudge: judgeName,
        presentationExtractor: "deepinfra-deepseek",
        newsSearchCollectorName: "newssearch");

    private static NewsJudgmentGenerator Generator(
        CountingAnalyzer analyzer,
        INewsJudgmentStore store,
        NewsJudgmentOptions? options = null,
        string judgeName = "deepinfra-deepseek",
        string modelId = "judge-model") =>
        new(
            new NullBatchReader(),
            new NewsJudgmentReaderSet(
            [
                new NewsJudgmentReader(
                    new NewsJudgmentReaderIdentity(judgeName, "openai", modelId), analyzer),
            ]),
            store,
            options ?? Options(),
            TimeProvider.System,
            NullLogger<NewsJudgmentGenerator>.Instance);

    private static NewsJudgmentCandidatePlan Plan(NewsJudgmentOptions? options = null) =>
        new NewsJudgmentCandidatePlanner(options ?? Options()).Plan(
        [
            NewsRiskTestData.Section(
                "disclosure-led-v11",
                isPrimary: true,
                StrategyPurpose.Research,
                NewsRiskTestData.Row(1, Eose, "Eos Energy", "EOSE")),
        ]);

    private static NewsTypingRunResult Typing(
        Guid? runId, string statement = "A regulator confirmed a filing against Eos Energy.")
    {
        var factRef = NewsJudgmentTestData.FactRef(
            Eose,
            Guid.Parse("f0000000-0000-4000-8000-000000000001"),
            statement,
            assertionStatus: NewsFactAssertionStatus.ConfirmedFiling,
            attribution: NewsFactAttribution.Regulator);
        var families = FactFamilyBuilder.Build(
        [
            new FactFamilyInputFact(
                FactId: factRef.Fact.FactId,
                CompanyId: Eose,
                EventTypes: factRef.Fact.EventTypes,
                Statement: statement,
                FirstObservedAtUtc: NewsJudgmentTestData.ObservedAt,
                Publisher: "Outlet",
                ObservationId: factRef.ObservationId,
                CaptureMode: NewsObservationCaptureMode.ProspectiveRss),
        ]);

        return new NewsTypingRunResult(
            RunId: runId,
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

    [Fact]
    public async Task ProviderCalls_AreBoundedAtMaxJudgmentAttempts_AcrossDistinctRuns()
    {
        var analyzer = new CountingAnalyzer(AlwaysInvalid);
        var store = new InsertOnlyStore();
        var generator = Generator(analyzer, store);

        NewsJudgmentRunResult? last = null;
        for (var run = 0; run < 5; run++)
        {
            var runId = Guid.NewGuid();
            last = await generator.GenerateAsync(runId, Plan(), Typing(runId), CancellationToken.None);
        }

        // Three hosted calls, no matter how many nights the run repeats.
        Assert.Equal(3, analyzer.Calls);

        // The fourth and fifth runs made NO call and said so, in the record and on the row.
        Assert.NotNull(last);
        Assert.Equal(
            "? unassessed (retries-exhausted)", last!.Markers!.Markers![Eose].CellText);
        var exhausted = store.Records.Where(
            r => r.Status == NewsJudgmentStatus.AttemptsExhausted).ToList();
        Assert.Equal(2, exhausted.Count);
        foreach (var record in exhausted)
        {
            // No model result rides an exhaustion record, and it never counts as a completed judgment or
            // as a spent call.
            Assert.Null(record.BusinessTrajectory);
            Assert.Null(record.RawResponseHash);
            Assert.Empty(record.Findings);
            Assert.False(record.IsCompletedJudgment);
            Assert.False(record.IsCallProducingAttempt);
            Assert.Contains("attempts-exhausted", record.FailureDetail!, StringComparison.Ordinal);
        }

        // Each later run got its OWN exhaustion record (distinct ids under distinct run scopes), so the
        // marker is `retries-exhausted` rather than a prior run's `stale`.
        Assert.Equal(2, exhausted.Select(r => r.JudgmentId).Distinct().Count());
        Assert.Equal(3, store.Records.Count(r => r.IsCallProducingAttempt));
    }

    [Fact]
    public async Task SameRunReEntry_ReusesThePersistedAttempt_AndMakesNoSecondCall()
    {
        var analyzer = new CountingAnalyzer(AlwaysInvalid);
        var store = new InsertOnlyStore();
        var generator = Generator(analyzer, store);
        var runId = Guid.NewGuid();

        var first = await generator.GenerateAsync(runId, Plan(), Typing(runId), CancellationToken.None);
        var second = await generator.GenerateAsync(runId, Plan(), Typing(runId), CancellationToken.None);

        Assert.Equal(1, analyzer.Calls);
        Assert.Single(store.Records);
        // The reused attempt still belongs to THIS run, so the row is derived from it, not `stale`.
        Assert.Equal(
            first!.Markers!.Markers![Eose].CellText, second!.Markers!.Markers![Eose].CellText);
        Assert.Equal("? unassessed (validation-failed)", second.Markers.Markers![Eose].CellText);

        // Spec 188 §1: the reused record IS the durable one — same instance, same non-null
        // ProviderDurationMs. The pass-local "did THIS invocation call the provider?" fact rides beside the
        // record (see NewsJudgmentProviderTimingTests) rather than being smuggled into it by nulling a
        // field the insert-only store already persisted.
        var stored = Assert.Single(store.Records);
        Assert.NotNull(stored.ProviderDurationMs);
        Assert.Same(stored, Assert.Single(second.Judgments));
    }

    [Fact]
    public async Task NullRunInvocations_MintDistinctStandaloneOrdinalIdentities_AndStayBounded()
    {
        // The spec-186 §2 precedent preserved: attempt 1 keeps the literal `standalone` token (so ids
        // already on disk are byte-unchanged) and later attempts are `standalone#N`. Without it every
        // standalone invocation minted ONE id, the insert-only store deduplicated the record, the derived
        // count never advanced — and the call budget was unbounded.
        var analyzer = new CountingAnalyzer(AlwaysInvalid);
        var store = new InsertOnlyStore();
        var generator = Generator(analyzer, store);

        for (var i = 0; i < 5; i++)
        {
            await generator.GenerateAsync(runId: null, Plan(), Typing(runId: null), CancellationToken.None);
        }

        Assert.Equal(3, analyzer.Calls);

        var attempts = store.Records.Where(r => r.IsCallProducingAttempt).ToList();
        Assert.Equal(3, attempts.Count);
        Assert.Equal(3, attempts.Select(r => r.JudgmentId).Distinct().Count());

        var cohortKey = attempts[0].CohortKey;
        var familySetHash = attempts[0].FamilySetHash;
        Assert.Equal(
            [
                NewsJudgmentRecord.IdentityFor(cohortKey, Eose, familySetHash, null, 1),
                NewsJudgmentRecord.IdentityFor(cohortKey, Eose, familySetHash, null, 2),
                NewsJudgmentRecord.IdentityFor(cohortKey, Eose, familySetHash, null, 3),
            ],
            attempts.Select(r => r.JudgmentId).ToList());

        // Attempt 1's id is exactly the pre-186 `standalone` id — no accrued identity moves.
        Assert.Equal(
            NewsJudgmentRecord.IdentityFor(cohortKey, Eose, familySetHash, null),
            attempts[0].JudgmentId);

        // Repeated exhausted null-run invocations idempotently reuse the ONE `standalone` exhaustion
        // record, so no call occurs and the row keeps saying why.
        var exhausted = Assert.Single(
            store.Records, r => r.Status == NewsJudgmentStatus.AttemptsExhausted);
        Assert.Equal(
            NewsJudgmentRecord.ExhaustionIdentityFor(cohortKey, Eose, familySetHash, null),
            exhausted.JudgmentId);

        // …and the exhaustion namespace can never collide with the last standalone#N CALL attempt.
        Assert.DoesNotContain(exhausted.JudgmentId, attempts.Select(r => r.JudgmentId));
    }

    [Fact]
    public async Task AChangedFamilySet_EarnsAFreshAttemptBudget()
    {
        // Deliberate (spec 187 §1): while the typing backlog drains a company's fact set grows, and a
        // materially changed INPUT is not the same question. The bound constrains repeated calls over the
        // same input, never the evaluation of newly available evidence.
        var analyzer = new CountingAnalyzer(AlwaysInvalid);
        var store = new InsertOnlyStore();
        var generator = Generator(analyzer, store);

        for (var run = 0; run < 4; run++)
        {
            var runId = Guid.NewGuid();
            await generator.GenerateAsync(runId, Plan(), Typing(runId), CancellationToken.None);
        }

        Assert.Equal(3, analyzer.Calls);

        var newRunId = Guid.NewGuid();
        await generator.GenerateAsync(
            newRunId,
            Plan(),
            Typing(newRunId, statement: "The regulator widened the confirmed filing to a second unit."),
            CancellationToken.None);

        Assert.Equal(4, analyzer.Calls);
    }

    [Fact]
    public async Task AChangedContract_EarnsAFreshAttemptBudget()
    {
        // The stage-2 cohort key folds the judge provider/model, the prompt/schema versions, the FULL
        // stage-1 cohort and the family-builder identity — so any of those changing is a different key and
        // therefore a different budget, by construction.
        var analyzer = new CountingAnalyzer(AlwaysInvalid);
        var store = new InsertOnlyStore();

        for (var run = 0; run < 4; run++)
        {
            var runId = Guid.NewGuid();
            await Generator(analyzer, store)
                .GenerateAsync(runId, Plan(), Typing(runId), CancellationToken.None);
        }

        Assert.Equal(3, analyzer.Calls);

        var freshRunId = Guid.NewGuid();
        await Generator(analyzer, store, modelId: "judge-model-v2")
            .GenerateAsync(freshRunId, Plan(), Typing(freshRunId), CancellationToken.None);

        Assert.Equal(4, analyzer.Calls);
    }

    [Fact]
    public async Task AnUnpersistedJudgment_NeverReachesPresentation()
    {
        // Spec 187 §1: the durable write's boolean is CHECKED. An unpersisted result is not a durable
        // judgment, so it never renders as judged or challenged — and never as `not-a-candidate`, which
        // would be a false claim about selection.
        var analyzer = new CountingAnalyzer(_ => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Unknown",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "Nothing in the supplied facts establishes a direction.",
                TrajectoryFactIds: []),
            "raw-hash",
            null));
        var store = new InsertOnlyStore { FailWrites = true };
        var runId = Guid.NewGuid();

        var result = await Generator(analyzer, store)
            .GenerateAsync(runId, Plan(), Typing(runId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.Judgments);
        Assert.Empty(store.Records);
        Assert.Equal("? unassessed (not-persisted)", result.Markers!.Markers![Eose].CellText);
    }

    [Fact]
    public async Task ACompletedJudgment_IsCachedNotRebudgeted()
    {
        // A cache reuse is not a hosted call, so it neither spends nor is spent by the attempt bound.
        var analyzer = new CountingAnalyzer(_ => new NewsJudgmentAnalysisOutcome(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Unknown",
                ChallengeStrength: null,
                Findings: [],
                Rationale: "Nothing in the supplied facts establishes a direction.",
                TrajectoryFactIds: []),
            "raw-hash",
            null));
        var store = new InsertOnlyStore();
        var generator = Generator(analyzer, store);

        for (var run = 0; run < 5; run++)
        {
            var runId = Guid.NewGuid();
            await generator.GenerateAsync(runId, Plan(), Typing(runId), CancellationToken.None);
        }

        Assert.Equal(1, analyzer.Calls);
        Assert.DoesNotContain(store.Records, r => r.Status == NewsJudgmentStatus.AttemptsExhausted);
        Assert.Equal(1, store.Records.Count(r => r.IsCallProducingAttempt));
        Assert.Equal(4, store.Records.Count(r => r.ReusedFromJudgmentId is not null));
    }
}
