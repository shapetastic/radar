using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.News;
using Radar.Application.NewsRisk;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.NewsTyping;
using Radar.Application.Reporting;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Application.Tests.Ai;
using Radar.Application.Tests.NewsRisk;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.NewsRisk.Judgment;

/// <summary>
/// SPEC 219 — judgment coverage becomes UNIVERSAL and the DEPTH is what gets capped.
/// <para>
/// The defect: the judge's only candidate source was the spec-179 §3 risk-audit traversal (top five rows
/// per Research section), so ~19 of 102 companies were read and the other ~83 reached scoring as a count of
/// articles with no direction. Because <c>RadarScoreFormulaV8</c> discounts Opportunity by Attention, heavy
/// UNREAD coverage lowered a rank, a lowered rank fell outside the top five, and the coverage was never
/// read — a loop that closed on itself. MarineMax (HZO) 2026-07-31, the largest miss in the store, was
/// never judged once.
/// </para>
/// </summary>
public sealed class NewsJudgmentBreadthCoverageTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static Guid CompanyId(int index) => JudgmentPassFixture.CompanyId(index);

    private static Guid FactId(int index) => JudgmentPassFixture.FactId(index);

    // ---------------------------------------------------------------- §1 the breadth ORDER

    [Fact]
    public void BreadthOrder_IsTickerAscendingThenCompanyId_AndBlankTickersSortLast()
    {
        var zulu = JudgmentPlanning.Company(CompanyId(1), "Zulu Co", "ZZZ");
        var alpha = JudgmentPlanning.Company(CompanyId(2), "Alpha Co", "AAA");
        var mike = JudgmentPlanning.Company(CompanyId(3), "Mike Co", "MMM");
        // Two untickered companies, deliberately given ids in the WRONG order relative to their names, so
        // the id tie-break is the only thing that can produce the asserted order.
        var untickeredLate = JudgmentPlanning.Company(CompanyId(9), "Private B", null);
        var untickeredEarly = JudgmentPlanning.Company(CompanyId(4), "Private A", "   ");

        var selected = NewsJudgmentCoveragePolicy.Select(
            [zulu, untickeredLate, mike, alpha, untickeredEarly],
            depthCompanyIds: new HashSet<Guid>(),
            remainingCapacity: 10);

        Assert.Equal(
            [alpha.Id, mike.Id, zulu.Id, untickeredEarly.Id, untickeredLate.Id],
            selected.Candidates.Select(c => c.CompanyId).ToList());
        Assert.Equal(0, selected.DroppedByCapacityValve);
    }

    [Fact]
    public void BreadthOrder_IsStableAcrossTwoSelectionsOverOneStore()
    {
        var universe = Enumerable
            .Range(0, 20)
            // Tickers deliberately DESCEND as the ids ascend, so an implementation that quietly fell back
            // to store order would produce a different list.
            .Select(i => JudgmentPlanning.Company(CompanyId(i), $"Co {i}", $"T{20 - i:D2}"))
            .ToList();

        var first = NewsJudgmentCoveragePolicy.Select(universe, new HashSet<Guid>(), 100);
        var second = NewsJudgmentCoveragePolicy.Select(universe, new HashSet<Guid>(), 100);

        Assert.Equal(
            first.Candidates.Select(c => c.CompanyId).ToList(),
            second.Candidates.Select(c => c.CompanyId).ToList());
        Assert.Equal(
            universe.OrderBy(c => c.Ticker, StringComparer.Ordinal).Select(c => c.Id).ToList(),
            first.Candidates.Select(c => c.CompanyId).ToList());
    }

    [Fact]
    public void TheBreadthPass_HasNoNotionOfBetter_SoItCarriesNoSelectionAncestry()
    {
        var selected = NewsJudgmentCoveragePolicy.Select(
            [JudgmentPlanning.Company(CompanyId(1), "Alpha Co", "AAA")], new HashSet<Guid>(), 10);

        // No strategy selected it, so no strategy/rank/snapshot is invented for it. "Shared ancestry, never
        // consensus" (spec 179 §3) reads here as "no ancestry at all", which a universe enumeration has.
        Assert.Empty(Assert.Single(selected.Candidates).Selections);
    }

    // ---------------------------------------------------------------- §1/§3 the PLAN

    [Fact]
    public void ACompanyInBothCohorts_AppearsOnceAtFullDepth_AndTypingsLaneStaysTheDepthCohort()
    {
        var alpha = JudgmentPlanning.Company(CompanyId(0), "Alpha Co", "ALPH");
        var breadthOnly = JudgmentPlanning.Company(CompanyId(1), "Beta Co", "BETA");

        var plan = JudgmentPlanning.Plan(Options(), Sections(alpha), alpha, breadthOnly);

        Assert.Equal([alpha.Id], plan.Candidates.Select(c => c.CompanyId).ToList());
        Assert.Equal([breadthOnly.Id], plan.BreadthCandidates.Select(c => c.CompanyId).ToList());
        Assert.Equal(
            [(alpha.Id, NewsJudgmentReadDepth.Full), (breadthOnly.Id, NewsJudgmentReadDepth.Breadth)],
            plan.PlannedCandidates.Select(p => (p.Candidate.CompanyId, p.Depth)).ToList());

        // Spec 219's NON-GOAL, asserted: the stage-1 typing priority lane is the DEPTH cohort and nothing
        // else. Breadth candidates must not spread one run's typing budget across the whole universe.
        Assert.Equal([alpha.Id], plan.CompanyIds);
        Assert.Equal(2, plan.CompaniesInUniverse);
    }

    [Fact]
    public void TheCapacityValve_CapsTheCombinedCohort_AndCountsWhatItDropped()
    {
        var depth = JudgmentPlanning.Company(CompanyId(0), "Alpha Co", "AAA");
        var universe = new[] { depth }
            .Concat(Enumerable.Range(1, 5).Select(i => JudgmentPlanning.Company(CompanyId(i), $"Co {i}", $"T{i:D2}")))
            .ToArray();

        var plan = JudgmentPlanning.Plan(Options(maxCompaniesPerRun: 3), Sections(depth), universe);

        Assert.Equal(3, plan.Count);
        Assert.Single(plan.Candidates);
        Assert.Equal(2, plan.BreadthCandidates.Count);
        // 6 in the universe, 1 in depth, 2 admitted => 3 dropped, COUNTED rather than silently absent.
        Assert.Equal(3, plan.BreadthDroppedByCapacityValve);
        Assert.Equal(3, plan.CapacityValve);
    }

    [Fact]
    public async Task AUniverseReadFailure_DegradesToNoBreadth_WithACountedWarning_NeverAbortingTheRun()
    {
        var logger = new CapturingLogger<NewsJudgmentCandidatePlanner>();
        var alpha = JudgmentPlanning.Company(CompanyId(0), "Alpha Co", "ALPH");
        var planner = new NewsJudgmentCandidatePlanner(
            Options(), new ThrowingCompanyRepository(), logger);

        var plan = await planner.PlanAsync(Sections(alpha), CancellationToken.None);

        Assert.Equal([alpha.Id], plan.Candidates.Select(c => c.CompanyId).ToList());
        Assert.Empty(plan.BreadthCandidates);
        // NOT RECORDED, never a fabricated 0 — the universe was never read.
        Assert.Null(plan.CompaniesInUniverse);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("could not read the company universe", warning.Message, StringComparison.Ordinal);
        Assert.Contains(NewsJudgmentCoveragePolicy.Version, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ACCEPTANCE-CRITERION regression, pinned to the shape it was found in: MarineMax (HZO) on
    /// 2026-07-31 — high Attention, rank ~43, twelve evidence links standing for ~75 collapsed articles of
    /// takeover coverage, +51.4% over the following 21 sessions, and NEVER JUDGED. Under the old
    /// top-five-per-section traversal it was not a candidate; under the breadth pass it is.
    /// </summary>
    [Fact]
    public void Hzo20260731_RankedFortyThird_WasNotATraversalCandidate_ButIsABreadthCandidate()
    {
        var hzo = JudgmentPlanning.Company(CompanyId(43), "MarineMax", "HZO");
        var universe = Enumerable
            .Range(1, 50)
            .Select(i => i == 43 ? hzo : JudgmentPlanning.Company(CompanyId(i), $"Co {i}", $"T{i:D2}"))
            .ToArray();
        var sections = new[]
        {
            NewsRiskTestData.Section(
                "disclosure-led-v11",
                isPrimary: true,
                StrategyPurpose.Research,
                [.. universe.Select((c, index) => NewsRiskTestData.Row(index + 1, c.Id, c.Name, c.Ticker))]),
        };

        // The pre-219 selection, run for real: five rows per section, so rank 43 is not in it.
        Assert.DoesNotContain(
            hzo.Id,
            NewsRiskCandidateSelector.Select(sections, 120).Select(c => c.CompanyId));

        var plan = JudgmentPlanning.Plan(Options(), sections, universe);

        Assert.Contains(hzo.Id, plan.BreadthCandidates.Select(c => c.CompanyId));
        Assert.Equal(
            NewsJudgmentReadDepth.Breadth,
            plan.PlannedCandidates.Single(p => p.Candidate.CompanyId == hzo.Id).Depth);
    }

    // ---------------------------------------------------------------- §2 DEPTH is what gets capped

    [Fact]
    public void TheShippedBreadthFamilyBound_IsFive()
    {
        // The measured arithmetic, not a projection: 322 accrued judgments supplied a median of 36 families
        // each, so ~19 companies cost ~610 family-units while 102 × 5 = 510.
        Assert.Equal(5, NewsJudgmentOptions.DefaultMaxFamiliesPerBreadthJudgment);
    }

    [Fact]
    public void TheFamilyCap_TieBreaksOnDistinctPublisherCount_BeforeFamilyId()
    {
        var companyId = CompanyId(0);
        var wide = NewsJudgmentTestData.FamilyRecord(
            companyId, FactId(1), "Widely corroborated claim.", memberCount: 4, distinctPublisherCount: 4)
            with
        { FamilyId = Guid.Parse("ffffffff-0000-4000-8000-000000000001") };
        var narrow = NewsJudgmentTestData.FamilyRecord(
            companyId, FactId(2), "Single-outlet claim.", memberCount: 4, distinctPublisherCount: 1)
            with
        { FamilyId = Guid.Parse("00000000-0000-4000-8000-000000000001") };
        var factsById = new Dictionary<Guid, NewsTypingFactRef>
        {
            [FactId(1)] = NewsJudgmentTestData.FactRef(companyId, FactId(1), "Widely corroborated claim."),
            [FactId(2)] = NewsJudgmentTestData.FactRef(companyId, FactId(2), "Single-outlet claim."),
        };

        var bundle = NewsJudgmentInputBuilder.Build(
            companyId, [narrow, wide], factsById, maxFamiliesPerJudgment: 1);

        // MUTATION-PROOF: the two families tie on MemberCount and the narrow one has the LOWER FamilyId, so
        // under the pre-219 order it would have been supplied. The publisher tie-break is the only reason
        // the wide one is.
        Assert.Equal(wide.FamilyId, Assert.Single(bundle.Families).FamilyId);
        Assert.Equal(2, bundle.FamiliesAvailable);
        Assert.Equal(NewsJudgmentFamilyBundle.Capped, bundle.FamilyBundle);
    }

    [Fact]
    public async Task ABreadthJudgment_IsBoundedAtTheBreadthCap_AndCountsWhatItWithheld()
    {
        var depth = JudgmentPlanning.Company(CompanyId(0), "Alpha Co", "AAA");
        var breadth = JudgmentPlanning.Company(CompanyId(1), "Beta Co", "BBB");
        // Eight distinct facts per company => eight families each.
        var facts = new List<JudgmentPassFact>();
        foreach (var (company, offset) in new[] { (depth, 0), (breadth, 100) })
        {
            facts.AddRange(Enumerable.Range(0, 8).Select(i => new JudgmentPassFact(
                company.Id,
                FactId(offset + i),
                $"A regulator confirmed filing {offset + i} against {company.Name}.")));
        }

        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(Grounded);
        var runId = Guid.NewGuid();

        var result = await generator.GenerateAsync(
            runId,
            JudgmentPlanning.Plan(Options(), Sections(depth), depth, breadth),
            JudgmentPassFixture.Typing(runId, facts),
            CancellationToken.None);

        var breadthRecord = Assert.Single(result!.Judgments, j => j.CompanyId == breadth.Id);
        Assert.Equal(NewsJudgmentReadDepth.Breadth, breadthRecord.ReadDepth);
        Assert.Equal(5, breadthRecord.Families.Count);
        Assert.Equal(8, breadthRecord.FamiliesAvailable);
        Assert.Equal(3, breadthRecord.FamiliesWithheldByBudget);
        Assert.Equal(NewsJudgmentFamilyBundle.Capped, breadthRecord.FamilyBundle);
        // The durable record states the bound that ACTUALLY cut it — 5, not the configured depth-cohort 50
        // that MaxFamiliesPerJudgment still (correctly) reports.
        Assert.Equal(5, breadthRecord.Limits.AppliedMaxFamilies);
        Assert.Equal(50, breadthRecord.Limits.MaxFamiliesPerJudgment);

        // The DEPTH cohort keeps the full 50-family budget: same code, different bound.
        var depthRecord = Assert.Single(result.Judgments, j => j.CompanyId == depth.Id);
        Assert.Equal(NewsJudgmentReadDepth.Full, depthRecord.ReadDepth);
        Assert.Equal(8, depthRecord.Families.Count);
        Assert.Equal(8, depthRecord.FamiliesAvailable);
        Assert.Equal(0, depthRecord.FamiliesWithheldByBudget);
        Assert.Equal(NewsJudgmentFamilyBundle.Complete, depthRecord.FamilyBundle);
        Assert.Equal(50, depthRecord.Limits.AppliedMaxFamilies);
    }

    // ---------------------------------------------------------------- §1/§4 skipped AND counted

    [Fact]
    public async Task ABreadthCompanyWithNoTypedFacts_IsSkippedAndCounted_AndPersistsNoRecord()
    {
        var depth = JudgmentPlanning.Company(CompanyId(0), "Alpha Co", "AAA");
        var withFacts = JudgmentPlanning.Company(CompanyId(1), "Beta Co", "BBB");
        var withoutFacts = JudgmentPlanning.Company(CompanyId(2), "Gamma Co", "CCC");

        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(Grounded);
        var runId = Guid.NewGuid();

        var result = await generator.GenerateAsync(
            runId,
            JudgmentPlanning.Plan(Options(), Sections(depth), depth, withFacts, withoutFacts),
            JudgmentPassFixture.Typing(
                runId,
                [
                    new JudgmentPassFact(depth.Id, FactId(0), "A regulator confirmed a filing against A."),
                    new JudgmentPassFact(withFacts.Id, FactId(1), "A regulator confirmed a filing against B."),
                ]),
            CancellationToken.None);

        Assert.Equal(
            [depth.Id, withFacts.Id],
            result!.Judgments.Select(j => j.CompanyId).Order().ToList());
        Assert.DoesNotContain(withoutFacts.Id, harness.Store.Records.Select(r => r.CompanyId));

        // Skipped is not silent: ONE aggregated per-cohort coverage line says so, names the coverage
        // policy, and RECONCILES — every planned candidate lands on exactly one counted axis.
        var coverageLine = Assert.Single(harness.Logger.Entries, e => e.Message.Contains(
            ") coverage (" + NewsJudgmentCoveragePolicy.Version + ")", StringComparison.Ordinal));
        Assert.Contains(
            "3 of 3 planned candidate(s) accounted for", coverageLine.Message, StringComparison.Ordinal);
        Assert.Contains("2 with typed facts in window", coverageLine.Message, StringComparison.Ordinal);
        Assert.Contains("1 skipped with none", coverageLine.Message, StringComparison.Ordinal);
        Assert.Contains("judged 1 breadth + 1 full = 2", coverageLine.Message, StringComparison.Ordinal);

        // The RUN-LEVEL facts are emitted ONCE, on their own line, so a two-cohort run cannot state the
        // universe size twice and invite a reader to double-count it.
        var runLine = Assert.Single(harness.Logger.Entries, e => e.Message.Contains(
            "run coverage (" + NewsJudgmentCoveragePolicy.Version + ")", StringComparison.Ordinal));
        Assert.Contains("3 company/companies in universe", runLine.Message, StringComparison.Ordinal);
        Assert.Contains(
            "0 breadth-eligible company/companies dropped by the capacity valve "
                + "Radar:NewsResearch:Judgment:MaxCompaniesPerRun=120",
            runLine.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("in universe", coverageLine.Message, StringComparison.Ordinal);

        // And the skipped company's ROW says the real condition. `not-a-candidate` would be a false claim
        // about selection — it WAS planned; it had nothing to read.
        Assert.Equal(
            "? unassessed (insufficient-facts)",
            result.Markers!.Markers![withoutFacts.Id].CellText);
    }

    [Fact]
    public async Task ADepthCompanyWithNoTypedFacts_StillPersistsInsufficientFacts_Unchanged()
    {
        // The spec-179 audit path is untouched by spec 219: a leader row about to be shown to a human must
        // still be able to name WHY it was not assessed.
        var depth = JudgmentPlanning.Company(CompanyId(0), "Alpha Co", "AAA");

        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(Grounded);
        var runId = Guid.NewGuid();

        var result = await generator.GenerateAsync(
            runId,
            JudgmentPlanning.Plan(Options(), Sections(depth), depth),
            JudgmentPassFixture.Typing(runId, []),
            CancellationToken.None);

        var record = Assert.Single(result!.Judgments);
        Assert.Equal(NewsJudgmentStatus.InsufficientFacts, record.Status);
        Assert.Equal(NewsJudgmentReadDepth.Full, record.ReadDepth);

        // ...and it is ACCOUNTED FOR on its own axis. Before this counter it was in neither
        // `with typed facts` nor `skipped`, so the coverage line silently described fewer candidates than
        // the pass had walked.
        var coverageLine = Assert.Single(harness.Logger.Entries, e => e.Message.Contains(
            ") coverage (" + NewsJudgmentCoveragePolicy.Version + ")", StringComparison.Ordinal));
        Assert.Contains(
            "1 of 1 planned candidate(s) accounted for", coverageLine.Message, StringComparison.Ordinal);
        Assert.Contains("1 recorded with none", coverageLine.Message, StringComparison.Ordinal);
        Assert.Contains("0 with typed facts in window", coverageLine.Message, StringComparison.Ordinal);
        Assert.Contains(
            "0 whose family accounting was not recorded", coverageLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompanySelectedByBothCohorts_IsJudgedExactlyOnce_AtFullDepth()
    {
        var both = JudgmentPlanning.Company(CompanyId(0), "Alpha Co", "AAA");

        var harness = new JudgmentPassHarness(AsOf);
        var generator = harness.Build(Grounded);
        var runId = Guid.NewGuid();

        var result = await generator.GenerateAsync(
            runId,
            JudgmentPlanning.Plan(Options(), Sections(both), both),
            JudgmentPassFixture.Typing(
                runId, [new JudgmentPassFact(both.Id, FactId(0), "A regulator confirmed a filing.")]),
            CancellationToken.None);

        var record = Assert.Single(result!.Judgments);
        Assert.Equal(both.Id, record.CompanyId);
        Assert.Equal(NewsJudgmentReadDepth.Full, record.ReadDepth);
        Assert.Equal(1, harness.Analyzer!.Calls);
    }

    [Fact]
    public void APreSpec219LimitsRecord_HydratesTheAppliedBoundAsNotRecorded_NeverAsAFabricatedValue()
    {
        // The positional shape a pre-219 file deserializes into. `AppliedMaxFamilies` is TRAILING and
        // NULLABLE, and MaxFamiliesPerJudgment keeps its ORIGINAL meaning (the configured depth-cohort
        // bound) — no accrued record is re-meant by spec 219.
        var accrued = new NewsJudgmentLimitsRecord(30, 50, 3);

        Assert.Null(accrued.AppliedMaxFamilies);
        Assert.Equal(50, accrued.MaxFamiliesPerJudgment);
    }

    // ---------------------------------------------------------------- §2 marker honesty

    [Fact]
    public void AFullOrPreSpec219Record_RendersByteIdenticallyToBeforeThisSlice()
    {
        var full = JudgedRecord(NewsJudgmentReadDepth.Full, familiesAvailable: 37, supplied: 37);
        var pre219 = JudgedRecord(readDepth: null, familiesAvailable: null, supplied: 37);

        Assert.Equal(
            "· no challenge found in supplied facts · trajectory improving",
            NewsJudgmentMarkerPolicy.Derive(full, full.RunId).CellText);
        Assert.Equal(
            "· no challenge found in supplied facts · trajectory improving",
            NewsJudgmentMarkerPolicy.Derive(pre219, pre219.RunId).CellText);
        Assert.Null(NewsJudgmentMarkerPolicy.Derive(full, full.RunId).ReadDepth);
        Assert.Null(NewsJudgmentMarkerPolicy.Derive(pre219, pre219.RunId).ReadDepth);
    }

    [Fact]
    public void ABreadthRecord_NamesItsBound_SoABoundedReadNeverRendersAsACompleteOne()
    {
        var breadth = JudgedRecord(NewsJudgmentReadDepth.Breadth, familiesAvailable: 37, supplied: 5);

        Assert.Equal(
            "· no challenge found in supplied facts · trajectory improving "
                + "· bounded read (5 of 37 families)",
            NewsJudgmentMarkerPolicy.Derive(breadth, breadth.RunId).CellText);
    }

    [Fact]
    public void ABreadthRecordWithNoRecordedDenominator_StillStatesTheBound()
    {
        var breadth = JudgedRecord(NewsJudgmentReadDepth.Breadth, familiesAvailable: null, supplied: 5);

        Assert.Equal(
            "· no challenge found in supplied facts · trajectory improving "
                + "· bounded read (5 families supplied, available not recorded)",
            NewsJudgmentMarkerPolicy.Derive(breadth, breadth.RunId).CellText);
    }

    [Fact]
    public void AnUnassessedRow_NeverCarriesAReadDepthToken()
    {
        // Nothing was read, so there is no bound to disclose — and a reason token must never be diluted.
        var marker = NewsJudgmentMarkerPolicy.Derive(
            JudgedRecord(NewsJudgmentReadDepth.Breadth, 37, 5) with
            {
                Status = NewsJudgmentStatus.ValidationFailed,
            },
            CurrentRunId);

        Assert.Equal("? unassessed (validation-failed)", marker.CellText);
        Assert.Null(marker.ReadDepth);
    }

    // ---------------------------------------------------------------- §6 / non-goals

    [Fact]
    public void TheCoveragePolicyVersion_IsUniversalCoverage_AndIsHashedThroughTheNewsSegment()
    {
        Assert.Equal("news-judgment-coverage-v2", NewsJudgmentCoveragePolicy.Version);

        // Read from the policy, never restated: the identity factory is the only production composer.
        Assert.Contains(
            NewsJudgmentCoveragePolicy.Version,
            NewsJudgmentScoringIdentityFactory.ForPresentationCohort("cohort-a").Segment,
            StringComparison.Ordinal);

        // ENABLED-ONLY: a judgment-disabled composition has no field for it, which is what makes the
        // AI-OFF fingerprints provably immovable by this slice.
        Assert.DoesNotContain(
            NewsJudgmentCoveragePolicy.Version,
            NewsJudgmentScoringIdentity.Disabled.Segment,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecordTagMoves_ButThePromptSchemaAndCohortKeyDoNot()
    {
        // Spec 219 changes how many companies are read and how deeply — not what the judge is asked. A
        // cohort-key move would have invalidated every cached verdict for no reason. (Spec 220 later moved the
        // tag to v9 and forked the cohort key for the family ORDER — its own cause; "coverage" still never
        // enters the key. Spec 221 then moved the tag to v10 and forked the prompt/schema for the
        // NoBusinessSignal verdict — its own cause, again not coverage.)
        Assert.Equal("news-judgment-v10", NewsJudgmentRecord.CurrentSchemaVersion);
        Assert.Equal("news-judgment-prompt-v7", NewsJudgmentContract.PromptVersion);
        Assert.Equal("news-judgment-schema-v5", NewsJudgmentContract.SchemaVersion);
        Assert.DoesNotContain(
            "coverage",
            NewsJudgmentContract.CohortKey("openai", "judge-model", "stage1"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewsExtraction_IsStillUnconditionallyNeutral_SoDirectionOnlyReachesScoringViaTheJudge()
    {
        // Spec 70/194 §1.1, ASSERTED rather than edited: spec 219 widens WHO the judge reads, and changes
        // nothing about how a collected article is extracted. If this ever went directional, the breadth
        // pass would be minting direction twice from one event.
        var evidence = new EvidenceBuilder()
            .WithSourceType(EvidenceSourceType.NewsArticle)
            .WithTitle("MarineMax surges on takeover approach")
            .WithSourceName("Example Wire")
            .WithCollectedAtUtc(AsOf)
            .Build();

        var output = await new KeywordSignalExtractor(
            NullLogger<KeywordSignalExtractor>.Instance, new InsiderMaterialityWeights())
            .ExtractAsync(evidence, CancellationToken.None);

        var signal = Assert.Single(output.Signals);
        Assert.Equal(SignalType.MediaAttention.ToString(), signal.SignalType);
        Assert.Equal(nameof(SignalDirection.Neutral), signal.Direction);
    }

    // ---------------------------------------------------------------- helpers

    private static readonly Guid CurrentRunId = Guid.Parse("11111111-0000-4000-8000-000000000001");

    private static NewsJudgmentOptions Options(int maxCompaniesPerRun = 120) => new(
        outputDirectory: "unused",
        maxCompaniesPerRun: maxCompaniesPerRun,
        maxFamiliesPerJudgment: 50,
        maxJudgmentAttempts: 3,
        maxFamiliesPerBreadthJudgment: NewsJudgmentOptions.DefaultMaxFamiliesPerBreadthJudgment,
        presentationJudge: JudgmentPassFixture.JudgeName,
        presentationExtractor: JudgmentPassFixture.ExtractorName,
        newsSearchCollectorName: "newssearch");

    /// <summary>One primary Research section whose rows are exactly the given depth companies.</summary>
    private static IReadOnlyList<StrategyReportSection> Sections(params Company[] depth) =>
    [
        NewsRiskTestData.Section(
            "disclosure-led-v11",
            isPrimary: true,
            StrategyPurpose.Research,
            [.. depth.Select((c, index) => NewsRiskTestData.Row(index + 1, c.Id, c.Name, c.Ticker))]),
    ];

    /// <summary>A judgment the v2 validator accepts: a directional call citing every supplied fact.</summary>
    private static NewsJudgmentAnalysisOutcome Grounded(NewsJudgmentAnalysisRequest request) =>
        new(
            NewsJudgmentAnalysisFailure.None,
            new NewsJudgmentModelResponse(
                BusinessTrajectory: "Deteriorating",
                ChallengeStrength: 3,
                Findings: [],
                Rationale: "The confirmed regulatory filing is adverse to the recent trajectory.",
                TrajectoryFactIds: [.. request.Families.Select(f => f.RepresentativeFactId.ToString("D"))]),
            "raw-hash",
            null);

    /// <summary>A same-run Judged/no-findings/Improving record with the spec-219 coverage fields set.</summary>
    private static NewsJudgmentRecord JudgedRecord(
        NewsJudgmentReadDepth? readDepth, int? familiesAvailable, int supplied)
    {
        var families = Enumerable
            .Range(0, supplied)
            .Select(i => new NewsJudgmentFamilyRef(FactId(i), FactId(i), 1, 1))
            .ToList();
        return new NewsJudgmentRecord(
            SchemaVersion: NewsJudgmentRecord.CurrentSchemaVersion,
            JudgmentId: Guid.Parse("22222222-0000-4000-8000-000000000001"),
            RunId: CurrentRunId,
            CompanyId: CompanyId(0),
            CompanyName: "Alpha Co",
            Ticker: "AAA",
            JudgeName: JudgmentPassFixture.JudgeName,
            Provider: "openai",
            ModelId: "judge-model",
            PromptVersion: NewsJudgmentContract.PromptVersion,
            ResultSchemaVersion: NewsJudgmentContract.SchemaVersion,
            Stage1CohortKey: "stage1",
            TaxonomyVersion: NewsTypingContract.TaxonomyVersion,
            TaxonomyHash: NewsEventTaxonomy.TaxonomyHash,
            FamilyBuilderIdentity: FactFamilyBuilder.IdentityString,
            CohortKey: "cohort",
            FamilySetHash: "hash",
            Families: families,
            ArchiveCapture: NewsRiskArchiveCapture.Proven,
            SearchEnumeration: NewsRiskSearchEnumeration.Complete,
            ObservationSupply: NewsRiskAssessmentBundle.Complete,
            TypingCompleteness: NewsTypingCompleteness.Complete,
            FamilyBundle: readDepth == NewsJudgmentReadDepth.Breadth
                ? NewsJudgmentFamilyBundle.Capped
                : NewsJudgmentFamilyBundle.Complete,
            CoverageIssues: [],
            Status: NewsJudgmentStatus.Judged,
            BusinessTrajectory: NewsJudgmentTrajectory.Improving,
            ChallengeStrength: 0,
            Findings: [],
            Rationale: "Nothing in the supplied facts challenges the trajectory.",
            FindingsTotal: 0,
            FindingsAccepted: 0,
            FindingsDropped: 0,
            FindingDropReasons: [],
            RawResponseHash: "raw",
            FailureDetail: null,
            Limits: new NewsJudgmentLimitsRecord(120, 50, 3),
            ReusedFromJudgmentId: null,
            CreatedAtUtc: AsOf,
            ReadDepth: readDepth,
            FamiliesAvailable: familiesAvailable,
            FamiliesWithheldByBudget: familiesAvailable is { } available
                ? Math.Max(0, available - supplied)
                : null);
    }

    /// <summary>A universe repository whose read FAILS — the degraded planning path (spec 219 §1).</summary>
    private sealed class ThrowingCompanyRepository : ICompanyRepository
    {
        public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct) =>
            throw new IOException("companies.json is unreadable");

        public Task AddAsync(Company company, CancellationToken ct) => throw new NotSupportedException();

        public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
