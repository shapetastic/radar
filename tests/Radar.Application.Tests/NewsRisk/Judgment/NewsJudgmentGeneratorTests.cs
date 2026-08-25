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
        StubAnalyzer analyzer, InMemoryJudgmentStore store, string judgeName = "deepinfra-deepseek") =>
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
            NullLogger<NewsJudgmentGenerator>.Instance);

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
        Assert.Equal("news-judgment-v3", reused.SchemaVersion);
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
}
