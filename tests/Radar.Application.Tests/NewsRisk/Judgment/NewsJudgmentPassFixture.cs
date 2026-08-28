using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.Tests.Ai;
using Radar.Application.Tests.NewsRisk;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>One supplied fact, and the company it owns, for <see cref="JudgmentPassFixture.Typing"/>.</summary>
internal readonly record struct JudgmentPassFact(Guid CompanyId, Guid FactId, string Statement);

/// <summary>
/// The shared builders for an end-to-end <see cref="NewsJudgmentGenerator"/> pass: options, the candidate
/// plan, and a stage-1 typing result carrying one canonical family per supplied fact.
/// <para>
/// EXTRACTED rather than copied a second time (spec 197 §2's slice): the spec-187 §7 provider-timing suite
/// and the spec-197 §2.2 citation-recovery suite both drive the REAL generator over the same shape, and two
/// copies of the plan/typing construction would let one suite's fixture drift from the other's while both
/// stayed green.
/// </para>
/// </summary>
internal static class JudgmentPassFixture
{
    public const string JudgeName = "deepinfra-deepseek";

    public const string ExtractorName = "deepinfra-deepseek";

    public static Guid CompanyId(int index) =>
        Guid.Parse(FormattableString.Invariant($"c0000000-0000-4000-8000-{index:D12}"));

    public static Guid FactId(int index) =>
        Guid.Parse(FormattableString.Invariant($"f0000000-0000-4000-8000-{index:D12}"));

    public static NewsJudgmentOptions Options() => new(
        outputDirectory: "unused",
        maxCompaniesPerRun: 50,
        maxFamiliesPerJudgment: 50,
        maxJudgmentAttempts: 3,
        presentationJudge: JudgeName,
        presentationExtractor: ExtractorName,
        newsSearchCollectorName: "newssearch");

    /// <summary>
    /// A plan over the given companies. The spec-179 selector takes at most
    /// <see cref="NewsRiskCandidateSelector.RowsPerSection"/> rows per Research section, so a larger
    /// candidate set is expressed the way a real multi-strategy run expresses it: more sections, the first
    /// primary and the rest ordinary Research arms.
    /// </summary>
    public static NewsJudgmentCandidatePlan Plan(IReadOnlyList<Guid> companyIds)
    {
        var sections = new List<StrategyReportSection>();
        for (var start = 0; start < companyIds.Count; start += NewsRiskCandidateSelector.RowsPerSection)
        {
            var rows = Enumerable
                .Range(start, Math.Min(NewsRiskCandidateSelector.RowsPerSection, companyIds.Count - start))
                .Select(i => NewsRiskTestData.Row(
                    i - start + 1, companyIds[i], FormattableString.Invariant($"Company {i}"), "TST"))
                .ToArray();
            sections.Add(NewsRiskTestData.Section(
                FormattableString.Invariant($"arm-{start / NewsRiskCandidateSelector.RowsPerSection}"),
                isPrimary: start == 0,
                StrategyPurpose.Research,
                rows));
        }

        return new NewsJudgmentCandidatePlanner(Options()).Plan(sections);
    }

    /// <summary>A plan of exactly <paramref name="companies"/> candidates using <see cref="CompanyId"/>.</summary>
    public static NewsJudgmentCandidatePlan Plan(int companies) =>
        Plan([.. Enumerable.Range(0, companies).Select(CompanyId)]);

    /// <summary>One canonical family per supplied fact, one stage-1 cohort, typed Complete.</summary>
    public static NewsTypingRunResult Typing(Guid? runId, IReadOnlyList<JudgmentPassFact> facts)
    {
        var factsById = new Dictionary<Guid, NewsTypingFactRef>();
        var inputs = new List<FactFamilyInputFact>();
        foreach (var fact in facts)
        {
            var factRef = NewsJudgmentTestData.FactRef(
                fact.CompanyId,
                fact.FactId,
                fact.Statement,
                assertionStatus: NewsFactAssertionStatus.ConfirmedFiling,
                attribution: NewsFactAttribution.Regulator);
            factsById[factRef.Fact.FactId] = factRef;
            inputs.Add(new FactFamilyInputFact(
                FactId: factRef.Fact.FactId,
                CompanyId: fact.CompanyId,
                EventTypes: factRef.Fact.EventTypes,
                Statement: fact.Statement,
                FirstObservedAtUtc: NewsJudgmentTestData.ObservedAt,
                Publisher: "Outlet",
                ObservationId: factRef.ObservationId,
                CaptureMode: NewsObservationCaptureMode.ProspectiveRss));
        }

        return new NewsTypingRunResult(
            RunId: runId,
            WindowStartUtc: NewsJudgmentTestData.ObservedAt.AddDays(-30),
            WindowEndUtc: NewsJudgmentTestData.ObservedAt.AddDays(1),
            NewsObservationBatchId: null,
            Cohorts:
            [
                new NewsTypingCohortRunResult(
                    Reader: new NewsTypingReaderIdentity(
                        ExtractorName, "openai", "deepseek-ai/DeepSeek-V4-Flash"),
                    Families: FactFamilyBuilder.Build(inputs),
                    FactsById: factsById,
                    TypingCompletenessByCompany: facts
                        .Select(f => f.CompanyId)
                        .Distinct()
                        .ToDictionary(id => id, _ => NewsTypingCompleteness.Complete),
                    FactsDroppedInWindow: 0,
                    RetryExhausted: 0),
            ]);
    }

    /// <summary>One fact per company, statements distinct so each company gets its own family.</summary>
    public static NewsTypingRunResult Typing(Guid? runId, int companies) => Typing(
        runId,
        [.. Enumerable.Range(0, companies).Select(i => new JudgmentPassFact(
            CompanyId(i),
            FactId(i),
            FormattableString.Invariant($"A regulator confirmed a filing against Company {i}.")))]);
}

/// <summary>
/// The shared end-to-end generator harness: a mutable monotonic clock, the insert-only store, a capturing
/// logger and a call-counting analyzer. <see cref="Build"/>'s optional latency script advances the clock
/// from INSIDE the analyzer, so a timing test never sleeps on the wall clock.
/// </summary>
internal sealed class JudgmentPassHarness(DateTimeOffset now, InsertOnlyStore? store = null)
{
    public MutableTimeProvider Time { get; } = new(now);

    /// <summary>
    /// The durable store. Supplying one lets a second pass run over the SAME accrued records — how the
    /// same-run and cache reuse paths are exercised.
    /// </summary>
    public InsertOnlyStore Store { get; } = store ?? new InsertOnlyStore();

    public CapturingLogger<NewsJudgmentGenerator> Logger { get; } = new();

    public CountingAnalyzer? Analyzer { get; private set; }

    public NewsJudgmentGenerator Build(
        Func<NewsJudgmentAnalysisRequest, NewsJudgmentAnalysisOutcome> respond,
        Func<int, TimeSpan>? latency = null)
    {
        Analyzer = new CountingAnalyzer(respond)
        {
            OnCall = latency is null ? null : call => Time.AdvanceTimestamp(latency(call)),
        };

        return new NewsJudgmentGenerator(
            new NullBatchReader(),
            new NewsJudgmentReaderSet(
            [
                new NewsJudgmentReader(
                    new NewsJudgmentReaderIdentity(JudgmentPassFixture.JudgeName, "openai", "judge-model"),
                    Analyzer),
            ]),
            Store,
            JudgmentPassFixture.Options(),
            Time,
            Logger);
    }
}
