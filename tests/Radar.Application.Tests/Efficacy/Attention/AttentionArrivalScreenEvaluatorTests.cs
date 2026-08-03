using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Collectors;
using Radar.Application.Efficacy.Attention;
using Radar.Application.Pipeline;
using Radar.Application.Scoring;
using Radar.Domain.Companies;
using Radar.TestSupport;

namespace Radar.Application.Tests.Efficacy.Attention;

/// <summary>
/// AD-16 §7's screen, end to end (spec 169). The fixture builds a synthetic universe with daily complete
/// coverage checkpoints, exact-time snapshots and per-day <c>MediaAttention</c> evidence, so every rule the
/// screen enforces can be exercised without touching a disk.
/// </summary>
public sealed class AttentionArrivalScreenEvaluatorTests
{
    // Well past AD-16 §4's precommitted boundary of 2026-09-29, so the eligibility tests are about the rule
    // under test rather than about the boundary.
    private static readonly DateTimeOffset FirstCandidate = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------------------------------
    // Availability prerequisites
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnavailableCohortConfiguration_SuppressesTheStatus_AndIsNotReportedAsPending()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1);
        fixture.Cohorts = new FakeExcludedCohortStore(
            ExcludedCohortSet.Unavailable("The cohorts directory 'docs/cohorts' does not exist."));

        var result = await fixture.EvaluateAsync();

        Assert.Equal(AttentionEvaluationAvailability.Unavailable, result.Availability);
        Assert.Equal(
            AttentionEvaluationUnavailableReason.CohortConfigurationUnavailable, result.UnavailableReason);
        // A configuration failure must NEVER be mislabelled as accrual.
        Assert.Null(result.ScreenStatus);
        Assert.Empty(result.Primary.Dates);
    }

    [Fact]
    public async Task AnEnabledSecondAttentionCollector_SuppressesTheStatus()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1);
        fixture.Vocabulary = EnabledCollectorVocabulary.FromNames(
            [AttentionTestFakes.NewsSearch, AttentionTestFakes.Gdelt]);

        var result = await fixture.EvaluateAsync();

        Assert.Equal(
            AttentionEvaluationUnavailableReason.UnsupportedAttentionCollector, result.UnavailableReason);
        Assert.Null(result.ScreenStatus);
        Assert.Contains(AttentionTestFakes.Gdelt, result.UnavailableDetail);
    }

    [Fact]
    public async Task AnUnconfiguredPrimaryArm_SuppressesTheStatus()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { IncludePrimaryStrategy = false };

        var result = await fixture.EvaluateAsync();

        Assert.Equal(
            AttentionEvaluationUnavailableReason.PrimaryStrategyNotConfigured, result.UnavailableReason);
        Assert.Null(result.ScreenStatus);
    }

    [Fact]
    public async Task ACohortMemberWhoseCikContradictsTheSeededCompany_SuppressesTheStatus()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1);
        // The fixture seeds every company with a CIK derived from its index; declaring a different one for a
        // ticker Radar DOES watch means the cohort and the universe disagree about who is being excluded.
        fixture.Cohorts = new FakeExcludedCohortStore(ExcludedCohortSet.Available(
            [new ExcludedCohortMember("event-enriched-2026-07", "TIC00", "0009999999")]));

        var result = await fixture.EvaluateAsync();

        Assert.Equal(
            AttentionEvaluationUnavailableReason.CohortConfigurationUnavailable, result.UnavailableReason);
        Assert.Null(result.ScreenStatus);
    }

    [Fact]
    public async Task ACohortMemberRadarDoesNotWatch_IsNotAContradiction()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1);
        fixture.Cohorts = new FakeExcludedCohortStore(ExcludedCohortSet.Available(
            [new ExcludedCohortMember("event-enriched-2026-07", "NOTSEEDED", "0000000001")]));

        var result = await fixture.EvaluateAsync();

        // A cohort may legitimately name a company before it is seeded; it simply excludes nothing.
        Assert.Equal(AttentionEvaluationAvailability.Available, result.Availability);
        Assert.Equal(20, result.Primary.Dates[0].CompaniesIncluded);
    }

    // -------------------------------------------------------------------------------------------------
    // Per-date rules
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADateWithFullCoverageAndTwentyCompanies_IsEligible_AndPairsBothCorrelationsOnTheSameSet()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1);

        var result = await fixture.EvaluateAsync();
        var row = Assert.Single(result.Primary.Dates);

        Assert.True(row.IsEligible);
        Assert.Equal(AttentionDateExclusionReason.None, row.ExclusionReason);
        Assert.Equal(20, row.CompaniesIncluded);
        Assert.Equal(20, row.Observations.Count);
        Assert.Empty(row.Exclusions);

        Assert.True(row.PrimaryCorrelation.IsDefined);
        Assert.True(row.PersistenceCorrelation.IsDefined);
        Assert.True(row.IsDeltaDefined);
        Assert.Equal(row.PrimaryCorrelation.Rho - row.PersistenceCorrelation.Rho, row.Delta, 12);

        // The as-of label is the DATE, but the metric anchor is the exact instant.
        Assert.Equal(DateOnly.FromDateTime(FirstCandidate.UtcDateTime), row.AsOfDateUtc);
        Assert.Equal(FirstCandidate, row.AsOfInstantUtc);
    }

    [Fact]
    public async Task FewerThanTwentyCompanies_ExcludesTheDate()
    {
        var fixture = new ScreenFixture(companies: 19, candidateDates: 1);

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.False(row.IsEligible);
        Assert.Equal(AttentionDateExclusionReason.InsufficientCompanies, row.ExclusionReason);
        Assert.False(row.IsDeltaDefined);
    }

    [Fact]
    public async Task TheCohortIsExcludedBEFORETheMinimumNIsCounted()
    {
        // 22 companies, two of them in the excluded cohort ⇒ 20 remain, which is exactly the minimum. If the
        // exclusion were applied after counting, this date would wrongly look like it had 22.
        var fixture = new ScreenFixture(companies: 22, candidateDates: 1);
        fixture.Cohorts = new FakeExcludedCohortStore(ExcludedCohortSet.Available(
        [
            new ExcludedCohortMember("event-enriched-2026-07", "TIC00", fixture.CikFor(0)),
            new ExcludedCohortMember("event-enriched-2026-07", "TIC01", fixture.CikFor(1)),
        ]));

        var result = await fixture.EvaluateAsync();
        var row = Assert.Single(result.Primary.Dates);

        Assert.True(row.IsEligible);
        // The three numbers RECONCILE on the page: 22 considered − 2 cohort = 20 included.
        Assert.Equal(22, row.CompaniesConsidered);
        Assert.Equal(2, row.CompaniesInExcludedCohort);
        Assert.Equal(20, row.CompaniesIncluded);
        Assert.DoesNotContain(row.Observations, o => o.Ticker is "TIC00" or "TIC01");

        // …and the exclusion is EMITTED per company rather than being a silent absence, so the drop is
        // auditable arithmetic in the artifact instead of something the reader has to infer.
        Assert.Equal(
            ["TIC00", "TIC01"],
            row.Exclusions
                .Where(e => e.Reason == AttentionCompanyExclusionReason.EventEnrichedCohort)
                .Select(e => e.Ticker)
                .Order(StringComparer.Ordinal));

        var cohortCount = Assert.Single(
            row.ExclusionCounts,
            c => c.Reason == nameof(AttentionCompanyExclusionReason.EventEnrichedCohort));
        Assert.Equal(2, cohortCount.Count);

        // The cohort is run through the SAME builders into its own section — beside, never pooled.
        var exploratory = Assert.Single(result.Exploratory.Dates);
        Assert.Equal(2, exploratory.CompaniesConsidered);
        // Two companies can never reach the minimum, so the exploratory rows can never satisfy the primary
        // N and can never change the primary status.
        Assert.False(exploratory.IsEligible);
        Assert.Equal(AttentionDateExclusionReason.InsufficientCompanies, exploratory.ExclusionReason);
        Assert.Equal(AttentionScreenStatus.Pending, result.ScreenStatus);
    }

    [Fact]
    public async Task DatesBeforeThePrecommittedBoundary_AreNotEligible_AndAreCountedAsSuch()
    {
        var fixture = new ScreenFixture(
            companies: 20,
            candidateDates: 1,
            firstCandidate: new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.False(row.IsEligible);
        Assert.Equal(AttentionDateExclusionReason.BeforeFirstEligibleDate, row.ExclusionReason);
        Assert.Equal(new DateOnly(2026, 9, 29), AttentionArrivalScreen.FirstEligibleAsOfDateUtc);
    }

    [Fact]
    public async Task AConstantOutcome_ExcludesTheDateUnderItsOwnReason()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { ConstantOutcome = true };

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.False(row.IsEligible);
        Assert.Equal(AttentionDateExclusionReason.ConstantOutcome, row.ExclusionReason);
        Assert.False(row.IsDeltaDefined);
        // Never NaN: an undefined coefficient is reported by name.
        Assert.False(row.PrimaryCorrelation.IsDefined);
        Assert.Equal("ConstantReturns", row.PrimaryCorrelation.UndefinedReason);
    }

    [Fact]
    public async Task AConstantPrimaryPredictor_ExcludesTheDateUnderItsOwnReason()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { ConstantPrimaryScore = true };

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.False(row.IsEligible);
        Assert.Equal(AttentionDateExclusionReason.ConstantPrimaryPredictor, row.ExclusionReason);
    }

    [Fact]
    public async Task AConstantSecondaryAttentionScore_MakesOnlyThatDiagnosticUndefined()
    {
        // AD-16 §6: the secondary is REPORTED, never screened on. A degenerate secondary cannot exclude a
        // date — otherwise a diagnostic would be quietly driving the binding result.
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { ConstantAttentionScore = true };

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.True(row.IsEligible);
        Assert.False(row.SecondaryAttentionScoreCorrelation.IsDefined);
        Assert.Equal("ConstantScores", row.SecondaryAttentionScoreCorrelation.UndefinedReason);
    }

    [Fact]
    public async Task AMissingControlSnapshotForOneCompany_ReportsIncompleteControlSupport_AndNothingElseMoves()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { OmitControlSnapshotForCompany = 3 };

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.True(row.IsEligible);
        Assert.False(row.ControlCorrelation.IsDefined);
        Assert.Equal("IncompleteControlSupport", row.ControlCorrelation.UndefinedReason);
        Assert.False(row.IsPrimaryMinusControlDefined);
        // The primary statistic is untouched: a diagnostic can never alter the binding number.
        Assert.True(row.IsDeltaDefined);
    }

    [Fact]
    public async Task AMissingBaselineSnapshot_ReportsIncompleteBASELINESupport_NotTheControlsToken()
    {
        // The token is what spec 155's joint-support gate will read: naming a baseline's shortfall
        // "IncompleteControlSupport" would misidentify which arm was short.
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { OmitBaselineSnapshotForCompany = 5 };

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.True(row.IsEligible);
        Assert.All(row.BaselineCorrelations, d =>
        {
            Assert.False(d.IsDefined);
            Assert.Equal(AttentionArrivalScreenEvaluator.IncompleteBaselineSupport, d.UndefinedReason);
        });

        // The v10 control keeps its own, distinct token.
        Assert.NotEqual(
            AttentionArrivalScreenEvaluator.IncompleteBaselineSupport,
            AttentionArrivalScreenEvaluator.IncompleteControlSupport);
        Assert.True(row.IsDeltaDefined);
    }

    [Fact]
    public async Task AnUnconfiguredBaselineArm_IsReportedAsSuch_AndCannotAlterTheStatus()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { IncludeBaselineStrategies = false };

        var result = await fixture.EvaluateAsync();
        var row = Assert.Single(result.Primary.Dates);

        Assert.True(row.IsEligible);
        Assert.Equal(3, row.BaselineCorrelations.Count);
        Assert.All(row.BaselineCorrelations, d =>
        {
            Assert.False(d.IsDefined);
            Assert.Equal("StrategyNotConfigured", d.UndefinedReason);
        });
        Assert.Equal(AttentionArrivalScreen.BaselineStrategyNames, row.BaselineCorrelations.Select(d => d.Name));
    }

    [Fact]
    public async Task BrokenCoverage_DropsTheCompanyAsIncompleteAttentionCollection_WithItsSpecificCause()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { CapOneCompanysFeeds = true };

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        Assert.Equal(19, row.CompaniesIncluded);
        var exclusion = Assert.Single(row.Exclusions);
        Assert.Equal(AttentionCompanyExclusionReason.IncompleteAttentionCollection, exclusion.Reason);
        Assert.Equal(AttentionCheckpointDisqualification.CompanyFeedCapped, exclusion.CoverageDetail);

        // The counted reason is rendered, so the drop is visible arithmetic rather than a silent shortfall.
        var count = Assert.Single(row.ExclusionCounts);
        Assert.Equal(nameof(AttentionCompanyExclusionReason.IncompleteAttentionCollection), count.Reason);
        Assert.Equal(1, count.Count);
    }

    [Fact]
    public async Task ASnapshotWithNoUnfilteredScoringRunAtItsExactWindowEnd_IsNotACandidate()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { FilterTheAnchorRun = true };

        var result = await fixture.EvaluateAsync();

        Assert.Empty(result.Primary.Dates);
        Assert.Equal(AttentionScreenStatus.Pending, result.ScreenStatus);
    }

    [Fact]
    public async Task AStandaloneScoreRunCanAnchorADate_WhenSeparateCollectRunsSupplyTheCoverage()
    {
        // AD-16's 2026-08-03 (ii) amendment, locked by a test: under a spec-144 split collect/score
        // schedule EVERY snapshot comes from a standalone score pass, so requiring the anchor run to have
        // collected would find zero candidates forever — silently, looking exactly like ordinary accrual.
        // Here the anchor run has Collectors = [] and CollectorRuns = null (it cannot supply a checkpoint
        // itself, asserted separately by ScoreOnlyRuns_CannotSupplyACheckpoint), and the coverage chain is
        // satisfied entirely by the separate collect runs.
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1) { SplitCollectAndScoreRuns = true };

        var row = Assert.Single((await fixture.EvaluateAsync()).Primary.Dates);

        // The date is admitted on FULL support: the split changed which run anchors, not what was proved.
        Assert.Equal(20, row.CompaniesIncluded);
        Assert.Empty(row.Exclusions);
    }

    [Fact]
    public async Task ASplitScheduleProducesTheSameScreenAsACombinedOne()
    {
        // The stronger claim: splitting the schedule is an OPERATIONAL choice that must not move the
        // precommitted metric. Same companies, same dates, same correlations, same delta.
        var combined = await new ScreenFixture(companies: 20, candidateDates: 1).EvaluateAsync();
        var split = await new ScreenFixture(companies: 20, candidateDates: 1)
            { SplitCollectAndScoreRuns = true }.EvaluateAsync();

        var a = Assert.Single(combined.Primary.Dates);
        var b = Assert.Single(split.Primary.Dates);

        Assert.Equal(a.AsOfInstantUtc, b.AsOfInstantUtc);
        Assert.Equal(a.CompaniesIncluded, b.CompaniesIncluded);
        Assert.Equal(a.PrimaryCorrelation, b.PrimaryCorrelation);
        Assert.Equal(a.PersistenceCorrelation, b.PersistenceCorrelation);
        Assert.Equal(a.IsDeltaDefined, b.IsDeltaDefined);
        Assert.Equal(a.Delta, b.Delta);
    }

    // -------------------------------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FewerThanTwentyEligibleDates_IsPending_NotAResult()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 19);

        var result = await fixture.EvaluateAsync();

        Assert.Equal(19, result.Primary.EligibleDates);
        Assert.Equal(AttentionScreenStatus.Pending, result.ScreenStatus);
    }

    [Fact]
    public async Task TwentyEligibleDatesWithANegativeMedianDelta_IsAMatureMiss()
    {
        // The fixture's primary score is deliberately ANTI-correlated with the outcome while the trailing
        // publisher count reproduces it exactly — the "attention is autocorrelated, so beating persistence is
        // the bar" case, failed.
        var fixture = new ScreenFixture(companies: 20, candidateDates: 20);

        var result = await fixture.EvaluateAsync();

        Assert.Equal(20, result.Primary.EligibleDates);
        Assert.True(result.Primary.IsMedianDeltaDefined);
        Assert.True(result.Primary.MedianDelta < 0.0);
        Assert.Equal(AttentionScreenStatus.Miss, result.ScreenStatus);
    }

    [Theory]
    [InlineData(new double[] { -1.0 }, -1.0)]
    [InlineData(new double[] { 3.0, 1.0, 2.0 }, 2.0)]
    // An EVEN count takes the mean of the two central values — stated because the convention matters to a
    // threshold test at exactly zero.
    [InlineData(new double[] { -1.0, 1.0 }, 0.0)]
    [InlineData(new double[] { 4.0, 1.0, 3.0, 2.0 }, 2.5)]
    public void Median_UsesTheMeanOfTheTwoCentralValuesOnAnEvenCount(double[] values, double expected) =>
        Assert.Equal(expected, AttentionArrivalScreenEvaluator.Median(values));

    [Fact]
    public void Median_OfNothing_IsUndefined() =>
        Assert.Null(AttentionArrivalScreenEvaluator.Median([]));

    // -------------------------------------------------------------------------------------------------
    // Artifacts
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IdenticalInputStores_ProduceByteIdenticalArtifacts()
    {
        // The whole point of forbidding wall-clock timestamps, machine paths and unordered collections: an
        // artifact that churns cannot be diffed, and a diff is how a change of meaning gets noticed.
        var first = await new ScreenFixture(companies: 20, candidateDates: 3).GenerateAsync();
        var second = await new ScreenFixture(companies: 20, candidateDates: 3).GenerateAsync();

        Assert.Equal(first.Json, second.Json);
        Assert.Equal(first.Csv, second.Csv);
        Assert.Equal(first.Markdown, second.Markdown);
    }

    [Fact]
    public async Task Artifacts_CarryTheBoundaryTheCoverageLimitationAndTheSeparateCohort_WithNoAdviceLanguage()
    {
        var written = await new ScreenFixture(companies: 20, candidateDates: 3).GenerateAsync();

        Assert.Contains("2026-09-29", written.Markdown);
        Assert.Contains("not a claim", written.Markdown);
        Assert.Contains("Exploratory cohort", written.Markdown);
        Assert.Contains("no confidence or significance claim", written.Markdown);

        // The exploratory section states plainly that a permanent `InsufficientCompanies` is the DESIGN, so
        // a reader does not file the intended behaviour as a defect.
        Assert.Contains("permanently", written.Markdown);
        Assert.Contains("COUNTS ONLY", written.Markdown);
        Assert.Contains("not a defect", written.Markdown);

        // The three screen tokens are verbatim in JSON (the machine-readable source of truth).
        Assert.Contains("\"screenStatus\": \"Pending\"", written.Json);
        Assert.Contains("\"asOfInstantUtc\"", written.Json);

        // CSV: one row per candidate date per section, with the header naming every reported statistic.
        var lines = written.Csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("rhoPrimary", lines[0]);
        Assert.Contains("rhoPersistence", lines[0]);
        Assert.Contains("delta", lines[0]);
        Assert.Contains("exclusionCounts", lines[0]);
        // header + 3 primary rows + the SAME 3 candidate dates in the exploratory section. The exploratory
        // rows are emitted even over an empty cohort — symmetric sections make "the cohort was considered and
        // held nobody" visible, rather than indistinguishable from "the section was never run".
        Assert.Equal(1 + 3 + 3, lines.Length);
        Assert.Equal(3, lines.Count(l => l.StartsWith("primary,", StringComparison.Ordinal)));
        Assert.Equal(
            3, lines.Count(l => l.StartsWith("exploratory-event-enriched,", StringComparison.Ordinal)));

        string[] banned = ["buy", "sell", "guaranteed upside", "safe bet"];
        foreach (var word in banned)
        {
            Assert.DoesNotContain(word, written.Markdown, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AnUnavailableEvaluation_StillWritesAnHonestArtifactSayingSo()
    {
        var fixture = new ScreenFixture(companies: 20, candidateDates: 1);
        fixture.Cohorts = new FakeExcludedCohortStore(
            ExcludedCohortSet.Unavailable("The cohorts directory 'docs/cohorts' does not exist."));

        var written = await fixture.GenerateAsync();

        Assert.Contains("not evaluated", written.Markdown);
        Assert.Contains("CohortConfigurationUnavailable", written.Markdown);
        // A configuration problem is stated as one, explicitly NOT as accrual.
        Assert.Contains("not** a pending accrual", written.Markdown);
        Assert.Contains("\"screenStatus\": null", written.Json);
    }

    // =================================================================================================
    // Fixture
    // =================================================================================================

    /// <summary>
    /// A synthetic universe with daily complete coverage checkpoints, exact-time snapshots for every arm and
    /// one uniquely-published <c>MediaAttention</c> article per day per company on a per-company cycle.
    /// <para>
    /// The cycle length is the publisher count in ANY 21-day window (each residue appears exactly once), so
    /// company <c>i</c> has a comparator and an outcome of <c>i + 1</c> distinct publishers — a perfectly
    /// autocorrelated attention series, which is exactly the hard baseline AD-16 §6 says the arm must clear.
    /// The primary score is set ANTI-correlated with it, so the default fixture is a MISS.
    /// </para>
    /// </summary>
    private sealed class ScreenFixture
    {
        private const int WindowDays = 21;

        private readonly int _companyCount;
        private readonly int _candidateDates;
        private readonly DateTimeOffset _firstCandidate;
        private readonly List<Company> _companies = [];
        private readonly List<CompanySourceFeed> _feeds = [];

        public ScreenFixture(int companies, int candidateDates, DateTimeOffset? firstCandidate = null)
        {
            _companyCount = companies;
            _candidateDates = candidateDates;
            _firstCandidate = firstCandidate ?? FirstCandidate;

            for (var i = 0; i < companies; i++)
            {
                var company = AttentionTestFakes.Company(CompanyId(i), $"TIC{i:D2}");
                _companies.Add(company);
                _feeds.Add(new CompanySourceFeed(
                    Id: Guid.Parse($"f0000000-0000-0000-0000-{i:D12}"),
                    CompanyId: company.Id,
                    FeedType: "sec",
                    Name: "SEC filings",
                    Url: $"https://data.sec.gov/submissions/CIK{CikFor(i)}.json",
                    CreatedAtUtc: DateTimeOffset.UnixEpoch));
            }
        }

        public IExcludedCohortStore Cohorts { get; set; } = FakeExcludedCohortStore.Empty;

        public EnabledCollectorVocabulary Vocabulary { get; set; } =
            EnabledCollectorVocabulary.FromNames([AttentionTestFakes.NewsSearch, "sec-edgar"]);

        public bool IncludePrimaryStrategy { get; init; } = true;

        public bool IncludeBaselineStrategies { get; init; } = true;

        public bool ConstantOutcome { get; init; }

        public bool ConstantPrimaryScore { get; init; }

        public bool ConstantAttentionScore { get; init; }

        public int? OmitControlSnapshotForCompany { get; init; }

        public int? OmitBaselineSnapshotForCompany { get; init; }

        public bool CapOneCompanysFeeds { get; init; }

        public bool FilterTheAnchorRun { get; init; }

        /// <summary>
        /// Model a spec-144 SPLIT deployment: a standalone <c>score</c> pass (no collectors, no
        /// <c>CollectorRuns</c>) at each candidate instant, with the collection that proves coverage coming
        /// from SEPARATE collect runs two hours earlier. See AD-16's 2026-08-03 (ii) amendment.
        /// </summary>
        public bool SplitCollectAndScoreRuns { get; init; }

        public static Guid CompanyId(int index) => Guid.Parse($"c0000000-0000-0000-0000-{index:D12}");

        public string CikFor(int index) => (index + 1).ToString("D10");

        private IEnumerable<DateTimeOffset> Candidates() =>
            Enumerable.Range(0, _candidateDates).Select(d => _firstCandidate.AddDays(d));

        public async Task<AttentionArrivalScreenResult> EvaluateAsync() =>
            await BuildEvaluator().EvaluateAsync(CancellationToken.None);

        public async Task<(string Json, string Csv, string Markdown)> GenerateAsync()
        {
            var store = new RecordingAttentionArrivalArtifactStore();
            var generator = new AttentionArrivalScreenGenerator(
                BuildEvaluator(),
                new AttentionArrivalRenderer(),
                store,
                NullLogger<AttentionArrivalScreenGenerator>.Instance);

            await generator.GenerateAsync(CancellationToken.None);
            return Assert.Single(store.Written);
        }

        private AttentionArrivalScreenEvaluator BuildEvaluator()
        {
            var options = AttentionTestFakes.Options();
            var (signals, evidence) = BuildAttention();

            return new AttentionArrivalScreenEvaluator(
                BuildStrategySet(),
                BuildStores(),
                new FakeAttentionCompanyRepository(_companies, _feeds),
                new FakePipelineRunStore([.. BuildRuns()]),
                Cohorts,
                Vocabulary,
                new AttentionPublisherCountBuilder(
                    signals, evidence, new RecordedOnlyCollectorAttributionResolver(), options),
                new AttentionCoverageEvaluator(options),
                options);
        }

        private ScoringStrategySet BuildStrategySet()
        {
            var definitions = new List<ScoringStrategyDefinition>
            {
                // A primary is mandatory for the set's invariant; the screen never reads it.
                new("default", "default", new ScoringWeights(), IsPrimary: true),
                new(AttentionArrivalScreen.ControlStrategyName, "default", new ScoringWeights(), false),
            };

            if (IncludePrimaryStrategy)
            {
                definitions.Add(
                    new(AttentionArrivalScreen.PrimaryStrategyName, "default", new ScoringWeights(), false));
            }

            if (IncludeBaselineStrategies)
            {
                definitions.AddRange(AttentionArrivalScreen.BaselineStrategyNames
                    .Select(n => new ScoringStrategyDefinition(n, "default", new ScoringWeights(), false)));
            }

            return new ScoringStrategySet(definitions);
        }

        private FakeStrategyScoreSnapshotStoreSelector BuildStores()
        {
            var selector = new FakeStrategyScoreSnapshotStoreSelector();

            selector.With(AttentionArrivalScreen.PrimaryStrategyName, BuildStore(StoreKind.Primary));
            selector.With(AttentionArrivalScreen.ControlStrategyName, BuildStore(StoreKind.Control));
            foreach (var baseline in AttentionArrivalScreen.BaselineStrategyNames)
            {
                selector.With(baseline, BuildStore(StoreKind.Baseline));
            }

            return selector;
        }

        /// <summary>Which arm a fixture store stands in for; only the omission rules differ.</summary>
        private enum StoreKind
        {
            Primary,
            Control,
            Baseline,
        }

        private FakeScoreSnapshotFileStore BuildStore(StoreKind kind)
        {
            var store = new FakeScoreSnapshotFileStore();
            for (var i = 0; i < _companyCount; i++)
            {
                if (kind == StoreKind.Control && OmitControlSnapshotForCompany == i)
                {
                    continue;
                }

                if (kind == StoreKind.Baseline && OmitBaselineSnapshotForCompany == i)
                {
                    continue;
                }

                var index = i;
                var snapshots = Candidates()
                    .Select((asOf, d) => new ScoreSnapshotBuilder()
                        // Deterministic ids (AD-3): a minted Guid would make two runs over identical stores
                        // differ, which is exactly what the byte-identical-artifact test exists to catch.
                        .WithId(Guid.Parse($"50000000-0000-{d:D4}-{(int)kind:D4}-{index:D12}"))
                        .WithCompanyId(CompanyId(index))
                        // ANTI-correlated with the outcome by default: a higher index means MORE publishers
                        // but a LOWER score, so the default fixture is a miss.
                        .WithOpportunityScore(ConstantPrimaryScore ? 50 : _companyCount - index)
                        .WithAttentionScore(ConstantAttentionScore ? 7 : index + 1)
                        .WithWindow(asOf.AddDays(-60), asOf)
                        .WithCreatedAtUtc(asOf)
                        .Build())
                    .ToArray();

                store.With(CompanyId(i), snapshots);
            }

            return store;
        }

        /// <summary>
        /// Daily unfiltered runs spanning every candidate's comparator and outcome window plus the checkpoint
        /// tolerance, each a complete <c>newssearch</c> checkpoint for every company.
        /// </summary>
        private IEnumerable<PipelineRunRecord> BuildRuns()
        {
            var first = _firstCandidate.AddDays(-WindowDays - 2);
            var last = _firstCandidate.AddDays(_candidateDates + WindowDays + 2);
            var candidates = Candidates().ToHashSet();

            for (var instant = first; instant <= last; instant = instant.AddDays(1))
            {
                var coverage = new List<CollectorCompanyCoverage>();
                for (var i = 0; i < _companyCount; i++)
                {
                    var capped = CapOneCompanysFeeds && i == 0;
                    coverage.Add(new CollectorCompanyCoverage(
                        CompanyId(i),
                        ExpectedFeedCount: 1,
                        SuccessfulFeedCount: 1,
                        HitEffectiveResultLimit: capped,
                        Issues: capped ? [CollectionCoverageIssues.ResultLimitReached] : []));
                }

                var isAnchor = candidates.Contains(instant);

                if (SplitCollectAndScoreRuns)
                {
                    // The collect pass: it carries the coverage but scores nothing, so it can never anchor.
                    yield return AttentionTestFakes.Checkpoint(
                        instant.AddHours(-2), coverage, strategies: []);

                    // The score pass at the candidate instant: collectors EMPTY and CollectorRuns NULL, so it
                    // is a genuine standalone score run and cannot itself supply a checkpoint.
                    yield return AttentionTestFakes.Checkpoint(instant, coverage) with
                    {
                        Collectors = [],
                        CollectorRuns = null,
                    };
                    continue;
                }

                yield return AttentionTestFakes.Checkpoint(
                    instant,
                    coverage,
                    // The anchor run can be made a FILTERED pass to prove a partial run cannot anchor a date.
                    companyFilter: isAnchor && FilterTheAnchorRun ? ["TIC00"] : null);
            }
        }

        /// <summary>
        /// One uniquely-published article per day per company, emitted on the days where
        /// <c>(dayIndex + i) mod 21 &lt; cycle(i)</c>. Over ANY 21 consecutive days each residue occurs
        /// exactly once, so the distinct-publisher count in both windows is exactly <c>cycle(i)</c>.
        /// </summary>
        private (FakeSignalRepository Signals, FakeEvidenceRepository Evidence) BuildAttention()
        {
            var signals = new FakeSignalRepository();
            var evidence = new FakeEvidenceRepository();

            var first = _firstCandidate.AddDays(-WindowDays - 2);
            var last = _firstCandidate.AddDays(_candidateDates + WindowDays + 2);

            var day = 0;
            for (var instant = first; instant <= last; instant = instant.AddDays(1), day++)
            {
                for (var i = 0; i < _companyCount; i++)
                {
                    var cycle = ConstantOutcome ? 1 : i + 1;
                    if ((day + i) % WindowDays >= cycle)
                    {
                        continue;
                    }

                    var evidenceId = Guid.Parse($"e0000000-0000-{day:D4}-0000-{i:D12}");
                    evidence.With(AttentionTestFakes.NewsEvidence(
                        evidenceId, $"Publisher-{i}-{(day + i) % WindowDays}"));
                    signals.With(AttentionTestFakes.MediaAttentionSignal(
                        CompanyId(i), evidenceId, instant.AddHours(4)));
                }
            }

            return (signals, evidence);
        }
    }

    /// <summary>A read-only company repository serving both the universe and the seeded source feeds.</summary>
    private sealed class FakeAttentionCompanyRepository(
        IReadOnlyList<Company> companies, IReadOnlyList<CompanySourceFeed> feeds) : ICompanyRepository
    {
        public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct) => Task.FromResult(companies);

        public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) =>
            Task.FromResult(feeds);

        public Task AddAsync(Company company, CancellationToken ct) => throw new NotSupportedException();

        public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
