using Microsoft.Extensions.Logging;

using Radar.Application.Efficacy.EvidenceConfidence;
using Radar.Application.Filings;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

using static Radar.Application.Tests.Efficacy.EvidenceConfidence.EvidenceConfidenceTestUniverse;

namespace Radar.Application.Tests.Efficacy.EvidenceConfidence;

/// <summary>
/// The spec-225 reporter: which companies are measured (and every excluded one counted on its own axis), the
/// terms recomputed THROUGH the production body against the persisted score, and the held-median
/// counterfactual — computed, never applied — with its rank accounting.
/// </summary>
public sealed class EvidenceConfidenceDistributionReporterTests
{
    private static ScoringStrategySet StrategiesNamed(string name) =>
        new([new ScoringStrategyDefinition(name, "default", new ScoringWeights(), IsPrimary: true)]);

    /// <summary>
    /// The four-company universe whose held-median counterfactual flips exactly one pair. Every component is
    /// produced by the production primitives from the signal shapes below (attention 0, Small tier ⇒ the
    /// notedness discount is exactly 1.0), so the expected numbers are derivable by hand:
    /// A: EC 40 (0.6·Medium, 1 type), Trajectory 80 ⇒ Opportunity 32;
    /// B: EC 75 (0.8·High, 3 types), Trajectory 60 ⇒ 45;
    /// C: EC 60 (0.8·High, 1 type), Trajectory 52 ⇒ 31;
    /// D: EC 45 (0.6·Medium, 2 types), Trajectory 45 ⇒ 20.
    /// Median EC = (45 + 60) / 2 = 52.5 ⇒ held 53: A 42, B 32, C 28, D 24 — A and B swap, C and D stay.
    /// </summary>
    private static EvidenceConfidenceTestUniverse FourCompanies()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("AAA", 80, 0, [MediumPressRelease]);
        u.AddCompany("BBB", 60, 0, [HighFiling, HighNews, MediumInsider]);
        u.AddCompany("CCC", 52, 0, [HighFiling]);
        u.AddCompany("DDD", 45, 0, [MediumPressRelease, MediumInsider]);
        return u;
    }

    private static EvidenceConfidenceCompanyRow Row(EvidenceConfidenceDistributionReport report, string ticker) =>
        report.Rows.Single(r => r.Ticker == ticker);

    [Fact]
    public async Task StrategyAbsent_IsIdle_WithTheReason_AndSaysSoOnce()
    {
        var u = FourCompanies();
        u.Strategies = StrategiesNamed("disclosure-led-v11");
        var logger = new CapturingReporterLogger();

        var report = await u.BuildAsync(logger: logger);

        Assert.False(report.StrategyConfigured);
        Assert.Contains("default", report.StrategyNotConfiguredReason, StringComparison.Ordinal);
        Assert.Contains("disclosure-led-v11", report.StrategyNotConfiguredReason, StringComparison.Ordinal);
        Assert.Null(report.StrategyName);
        Assert.Null(report.InstantUtc);
        Assert.Empty(report.Rows);
        Assert.Equal(EvidenceConfidenceCounts.Empty, report.Counts);
        Assert.Equal(EvidenceConfidenceVerdict.NotDetermined, report.Verdict.Verdict);

        var idle = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Information, idle.Level);
        Assert.Contains("did NO work", idle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStrategyIsMatchedCaseInsensitively_LikeTheSet()
    {
        var u = FourCompanies();
        u.Strategies = StrategiesNamed("Default");

        var report = await u.BuildAsync();

        Assert.True(report.StrategyConfigured);
        Assert.Equal("Default", report.StrategyName);
    }

    [Fact]
    public async Task TheInstant_IsTheLatestWindowEndAcrossLatestSnapshots_AndOlderLatestsAreCountedNotAtInstant()
    {
        var u = FourCompanies();
        // A company whose LATEST snapshot ends a day earlier: an older read, excluded and counted.
        u.AddCompany("OLD", 50, 0, [HighFiling], windowEnd: Instant.AddDays(-1));
        // A company with an older AND a current snapshot: the current one is chosen (latest WindowEndUtc).
        var both = u.AddCompany("BTH", 50, 0, [HighFiling], windowEnd: Instant.AddDays(-3));
        u.AddSnapshot(both, 70, 0, [HighFiling, HighNews], Instant);

        var report = await u.BuildAsync();

        Assert.Equal(Instant, report.InstantUtc);
        Assert.Equal(Instant - Window, report.WindowStartUtc);
        Assert.Equal(EvidenceConfidenceCompanyState.SnapshotNotAtInstant, Row(report, "OLD").State);
        Assert.Equal(Instant.AddDays(-1), Row(report, "OLD").SnapshotWindowEndUtc);
        Assert.Null(Row(report, "OLD").EvidenceConfidencePersisted);
        Assert.Equal(EvidenceConfidenceCompanyState.Included, Row(report, "BTH").State);
        Assert.Equal(70, Row(report, "BTH").Trajectory);
        Assert.Equal(1, report.Counts.SnapshotNotAtInstant);
        Assert.Equal(5, report.Counts.CompaniesIncluded);
    }

    [Fact]
    public async Task ACompanyWithNoSnapshot_IsCountedNoSnapshot_AndRecordsNothing()
    {
        var u = FourCompanies();
        u.AddCompanyWithoutSnapshot("NEW");

        var report = await u.BuildAsync();

        var row = Row(report, "NEW");
        Assert.Equal(EvidenceConfidenceCompanyState.NoSnapshot, row.State);
        Assert.Null(row.EvidenceConfidencePersisted);
        Assert.Null(row.Trajectory);
        Assert.Null(row.ActualRank);
        Assert.Contains("NoSnapshot", row.Flags);
        Assert.Equal(1, report.Counts.NoSnapshot);
        Assert.Equal(4, report.Counts.CompaniesIncluded);
    }

    [Fact]
    public async Task AZeroLinkSnapshot_IsADefaultedZero_NotRecorded_AndOutOfEveryDistribution()
    {
        var u = FourCompanies();
        u.AddCompany("NIL", 0, 0, []);

        var report = await u.BuildAsync();

        var row = Row(report, "NIL");
        Assert.Equal(EvidenceConfidenceCompanyState.NoSignalsInWindow, row.State);
        Assert.Null(row.EvidenceConfidencePersisted);
        Assert.Null(row.EvidenceConfidenceRecomputed);
        Assert.Null(row.Trajectory);
        Assert.Null(row.Opportunity);
        Assert.Null(row.BestConfidence);
        Assert.Null(row.CounterfactualOpportunity);
        Assert.Equal(1, report.Counts.NoSignalsInWindow);
        Assert.Equal(4, report.Counts.CompaniesIncluded);
        Assert.Equal(4, report.Distributions.IncludedCount);
        // The persisted 0 never enters the distribution: no EvidenceConfidence value 0 is listed.
        Assert.DoesNotContain(report.Distributions.EvidenceConfidence, e => e.Value == 0);
        Assert.All(report.Correlations, c => Assert.Equal(4, c.N));
        Assert.Equal(4, report.Counterfactual.CompaniesRanked);

        var md = new EvidenceConfidenceDistributionRenderer().RenderMarkdown(report);
        var line = md.Split('\n').Single(l => l.StartsWith("| NIL |", StringComparison.Ordinal));
        Assert.Contains("(not recorded)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnresolvableLink_IsCountedPerLinkAndPerCompany_AndTheCompanyIsExcluded()
    {
        var u = FourCompanies();
        u.AddCompany("NOS", 50, 0, [HighFiling, HighNews], withholdFirstSignal: true);
        u.AddCompany("NOE", 50, 0, [HighFiling, HighNews, MediumInsider], withholdFirstEvidence: true);

        var report = await u.BuildAsync();

        var nos = Row(report, "NOS");
        Assert.Equal(EvidenceConfidenceCompanyState.CompanyWithUnresolvableLink, nos.State);
        Assert.Equal(2, nos.LinkCount);
        Assert.Equal(1, nos.LinkSignalUnresolvable);
        Assert.Equal(0, nos.LinkEvidenceUnresolvable);
        Assert.Null(nos.EvidenceConfidencePersisted);

        var noe = Row(report, "NOE");
        Assert.Equal(EvidenceConfidenceCompanyState.CompanyWithUnresolvableLink, noe.State);
        Assert.Equal(0, noe.LinkSignalUnresolvable);
        Assert.Equal(1, noe.LinkEvidenceUnresolvable);

        Assert.Equal(1, report.Counts.LinkSignalUnresolvable);
        Assert.Equal(1, report.Counts.LinkEvidenceUnresolvable);
        Assert.Equal(2, report.Counts.CompanyWithUnresolvableLink);
        Assert.Equal(4, report.Counts.CompaniesIncluded);
    }

    [Fact]
    public async Task TheRecomputedTerms_MatchThePersistedScore_WhenBothCameThroughTheProductionBody()
    {
        var report = await FourCompanies().BuildAsync();

        Assert.Equal(0, report.Counts.TermsDisagreeWithSnapshot);
        Assert.All(
            report.Rows,
            r =>
            {
                Assert.Equal(r.EvidenceConfidencePersisted, r.EvidenceConfidenceRecomputed);
                Assert.False(r.TermsDisagreeWithSnapshot);
            });

        // The hand-derivable values, as a check that the fixture means what its comment says.
        Assert.Equal(40, Row(report, "AAA").EvidenceConfidencePersisted);
        Assert.Equal(75, Row(report, "BBB").EvidenceConfidencePersisted);
        Assert.Equal(60, Row(report, "CCC").EvidenceConfidencePersisted);
        Assert.Equal(45, Row(report, "DDD").EvidenceConfidencePersisted);

        var bbb = Row(report, "BBB");
        Assert.Equal(0.8, bbb.BestConfidence);
        Assert.Equal(new ScoringWeights().QualityHigh, bbb.BestQualityWeight);
        Assert.Equal(3, bbb.DistinctSourceTypes);
        Assert.Equal(1.0, bbb.DiversityFactor);
        Assert.Equal(1, Row(report, "AAA").DistinctSourceTypes);
        Assert.Equal(1 / new ScoringWeights().DiversityTarget, Row(report, "AAA").DiversityFactor);
    }

    [Fact]
    public async Task APersistedScoreTheTermsDoNotReproduce_IsFlaggedAndCounted_KeptInTheDistribution_OutOfTheTermTables()
    {
        var u = FourCompanies();
        // Persisted 70 where the terms say 60: a finding, not an error.
        u.AddCompany("DIS", 50, 0, [HighFiling], persistedEvidenceConfidence: 70);

        var report = await u.BuildAsync();

        var row = Row(report, "DIS");
        Assert.Equal(EvidenceConfidenceCompanyState.Included, row.State);
        Assert.True(row.TermsDisagreeWithSnapshot);
        Assert.Equal(70, row.EvidenceConfidencePersisted);
        Assert.Equal(60, row.EvidenceConfidenceRecomputed);
        Assert.Contains("TermsDisagreeWithSnapshot", row.Flags);
        Assert.Equal(1, report.Counts.TermsDisagreeWithSnapshot);

        // Distribution uses the PERSISTED value (70 is listed; 60 has exactly one holder, CCC).
        Assert.Contains(report.Distributions.EvidenceConfidence, e => e.Value == 70 && e.Companies == 1);
        Assert.Contains(report.Distributions.EvidenceConfidence, e => e.Value == 60 && e.Companies == 1);
        Assert.Equal(5, report.Distributions.IncludedCount);
        // The term tables exclude it.
        Assert.Equal(4, report.Distributions.TermRowCount);
        Assert.Equal(4, report.Distributions.DistinctSourceTypes.Sum(e => e.Companies));
        Assert.Equal(4, report.Correlations.Single(c => c.Pair.EndsWith("DistinctSourceTypes", StringComparison.Ordinal)).N);
        Assert.Equal(5, report.Correlations.Single(c => c.Pair.EndsWith("Trajectory", StringComparison.Ordinal)).N);
    }

    [Fact]
    public async Task TheDistribution_ListsEveryDistinctPersistedValue_WithSharesOfTheIncludedCount()
    {
        var u = FourCompanies();
        u.AddCompany("EEE", 30, 0, [HighFiling]); // a second 60

        var report = await u.BuildAsync();

        var d = report.Distributions;
        Assert.Equal(5, d.IncludedCount);
        Assert.Equal(4, d.DistinctEvidenceConfidenceValues);
        Assert.Equal([40, 45, 60, 75], d.EvidenceConfidence.Select(e => e.Value).ToArray());
        Assert.Equal(2, d.EvidenceConfidence.Single(e => e.Value == 60).Companies);
        Assert.Equal(0.4, d.EvidenceConfidence.Single(e => e.Value == 60).Share);
        Assert.Equal(60, d.ModalEvidenceConfidence);
        Assert.Equal(0.4, d.ModalEvidenceConfidenceShare);
        Assert.Equal(60.0, d.MedianEvidenceConfidence);
    }

    [Fact]
    public async Task TheCounterfactual_HoldsTheMedian_AndCountsExactlyTheRanksThatMove()
    {
        var report = await FourCompanies().BuildAsync();

        var cf = report.Counterfactual;
        Assert.True(cf.Available);
        Assert.Equal(52.5, report.Distributions.MedianEvidenceConfidence);
        Assert.Equal(53, cf.HeldEvidenceConfidence);
        Assert.Equal(4, cf.CompaniesRanked);
        Assert.Equal(2, cf.RankChanged);
        Assert.Equal(0.5, cf.RankChangedShare);
        Assert.Equal(1, cf.MaxAbsRankDelta);
        Assert.Equal(0, cf.EnterTopN);
        Assert.Equal(0, cf.LeaveTopN);
        Assert.Equal(10, cf.TopN);
        Assert.Equal(31.5, cf.MedianActualOpportunity);
        Assert.Equal(30.0, cf.MedianCounterfactualOpportunity);

        var a = Row(report, "AAA");
        var b = Row(report, "BBB");
        Assert.Equal((32, 2, 42, 1, -1), (a.Opportunity, a.ActualRank, a.CounterfactualOpportunity, a.CounterfactualRank, a.RankDelta));
        Assert.Equal((45, 1, 32, 2, +1), (b.Opportunity, b.ActualRank, b.CounterfactualOpportunity, b.CounterfactualRank, b.RankDelta));
        Assert.Equal((3, 3), (Row(report, "CCC").ActualRank, Row(report, "CCC").CounterfactualRank));
        Assert.Equal((4, 4), (Row(report, "DDD").ActualRank, Row(report, "DDD").CounterfactualRank));

        // Rows come back in actual-rank order.
        Assert.Equal(["BBB", "AAA", "CCC", "DDD"], report.Rows.Select(r => r.Ticker!).ToArray());

        // ρ(EC, DistinctSourceTypes) = 3/sqrt(22.5) ≈ 0.632 ≥ 0.5 with rank-change share 0.5 and modal share
        // 0.25 ⇒ the rule reads (c): it varies, but with how many source types fired.
        Assert.Equal(EvidenceConfidenceVerdict.DiscriminatesTheWrongThing, report.Verdict.Verdict);
        Assert.Contains("(c)", report.Verdict.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APersistedOpportunityThatDoesNotRecompose_IsCountedAndExcludedFromTheRanking()
    {
        var u = FourCompanies();
        u.AddCompany("MIS", 50, 0, [HighFiling], persistedOpportunity: 99);

        var report = await u.BuildAsync();

        var row = Row(report, "MIS");
        Assert.Equal(EvidenceConfidenceCompanyState.Included, row.State);
        Assert.True(row.OpportunityRecompositionMismatch);
        Assert.Null(row.ActualRank);
        Assert.Null(row.CounterfactualOpportunity);
        Assert.Contains("OpportunityRecompositionMismatch", row.Flags);
        Assert.Equal(1, report.Counts.OpportunityRecompositionMismatch);
        Assert.Equal(5, report.Counts.CompaniesIncluded);
        Assert.Equal(4, report.Counts.CompaniesRanked);
        // Still in the distribution and the correlations (the persisted EC is real; only the ranking excludes it).
        Assert.Equal(5, report.Distributions.IncludedCount);
        // Unranked rows come after the ranked ones.
        Assert.Equal("MIS", report.Rows[^1].Ticker);
    }

    [Fact]
    public async Task ANonV8Formula_HasNoCounterfactual_CountsEveryIncludedCompany_AndIsNotDetermined()
    {
        var u = FourCompanies();
        u.Strategies = new ScoringStrategySet(
        [
            new ScoringStrategyDefinition("default", "default", new ScoringWeights(), IsPrimary: true)
            {
                Formula = ScoreFormulaVersions.V11,
                Channels = ScoringChannelSet.Create(
                    [ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3)], "default"),
            },
        ]);

        var report = await u.BuildAsync();

        Assert.False(report.FormulaIsV8);
        Assert.Equal(ScoreFormulaVersions.V11, report.Formula);
        Assert.False(report.Counterfactual.Available);
        Assert.Contains(ScoreFormulaVersions.V11, report.Counterfactual.NotAvailableReason, StringComparison.Ordinal);
        Assert.Equal(4, report.Counts.FormulaDoesNotComposeOpportunityFromEvidenceConfidence);
        Assert.Equal(0, report.Counts.CompaniesRanked);
        Assert.Equal(EvidenceConfidenceVerdict.NotDetermined, report.Verdict.Verdict);
        Assert.All(report.Rows, r => Assert.Null(r.ActualRank));
        // The distribution and terms are still measured — only the counterfactual needs v8's composition.
        Assert.Equal(4, report.Distributions.IncludedCount);
        Assert.Equal(0, report.Counts.TermsDisagreeWithSnapshot);
    }

    [Fact]
    public async Task ASingleRankedCompany_LeavesTheCounterfactualUnavailable_WithTheReason()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("ONE", 50, 0, [HighFiling]);

        var report = await u.BuildAsync();

        Assert.False(report.Counterfactual.Available);
        Assert.Contains("fewer than 2 ranked", report.Counterfactual.NotAvailableReason, StringComparison.Ordinal);
        Assert.Equal(60, report.Counterfactual.HeldEvidenceConfidence);
        Assert.Equal(1, report.Counterfactual.CompaniesRanked);
        Assert.Equal(EvidenceConfidenceVerdict.NotDetermined, report.Verdict.Verdict);
        // A single-point series has no rank variance: every correlation is undefined and says so.
        Assert.All(report.Correlations, c => { Assert.Null(c.Rho); Assert.Contains("fewer than 2", c.UndefinedReason, StringComparison.Ordinal); });
    }

    [Fact]
    public async Task AConstantSeries_YieldsAnUndefinedCorrelation_WithTheSideNamed()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("AAA", 80, 0, [HighFiling]);
        u.AddCompany("BBB", 60, 0, [HighFiling]);
        u.AddCompany("CCC", 40, 0, [HighFiling]);

        var report = await u.BuildAsync();

        var traj = report.Correlations.Single(c => c.Pair == "EvidenceConfidence vs Trajectory");
        Assert.Null(traj.Rho);
        Assert.Equal("undefined: constant series (EvidenceConfidence)", traj.UndefinedReason);
        Assert.Equal(3, traj.N);
        // Every company holds the same value ⇒ modal share 1.0 ⇒ a flat tax by the rule, and the counterfactual
        // (held at that very value) moves nobody.
        Assert.Equal(1.0, report.Distributions.ModalEvidenceConfidenceShare);
        Assert.Equal(0, report.Counterfactual.RankChanged);
        Assert.Null(report.Counterfactual.MaxAbsRankDeltaCompany);
        Assert.Equal(EvidenceConfidenceVerdict.FlatTax, report.Verdict.Verdict);
    }

    [Fact]
    public async Task MixedScoringConfigVersions_AtTheInstant_AreCountedAndListed()
    {
        var u = FourCompanies();
        u.AddCompany("OTH", 50, 0, [HighFiling], scoringConfigVersion: "radar-scoring-fp-other");
        u.AddCompany("NUL", 50, 0, [HighFiling], scoringConfigVersion: null);

        var report = await u.BuildAsync();

        Assert.Equal(3, report.Counts.MixedScoringConfigVersion);
        Assert.Equal(["(not recorded)", "radar-scoring-fp-other", "radar-scoring-fp-test"], report.ScoringConfigVersionsSeen);
        Assert.Equal(6, report.Counts.CompaniesIncluded);
    }

    [Fact]
    public async Task TheCountedAxes_Reconcile_AcrossEveryExclusionKind()
    {
        var u = FourCompanies();
        u.AddCompanyWithoutSnapshot("NEW");
        u.AddCompany("OLD", 50, 0, [HighFiling], windowEnd: Instant.AddDays(-1));
        u.AddCompany("NIL", 0, 0, []);
        u.AddCompany("NOS", 50, 0, [HighFiling, HighNews], withholdFirstSignal: true);
        u.AddCompany("DIS", 50, 0, [HighFiling], persistedEvidenceConfidence: 70);
        u.AddCompany("MIS", 50, 0, [HighFiling], persistedOpportunity: 99);

        var report = await u.BuildAsync();

        var c = report.Counts;
        Assert.Equal(10, c.CompaniesSeeded);
        Assert.Equal(6, c.CompaniesIncluded);
        Assert.Equal(5, c.CompaniesRanked);
        Assert.Equal(1, c.NoSnapshot);
        Assert.Equal(1, c.SnapshotNotAtInstant);
        Assert.Equal(1, c.NoSignalsInWindow);
        Assert.Equal(1, c.CompanyWithUnresolvableLink);
        Assert.Equal(1, c.TermsDisagreeWithSnapshot);
        Assert.Equal(1, c.OpportunityRecompositionMismatch);
        Assert.True(c.Reconciles);
        Assert.Equal(
            c.CompaniesSeeded,
            c.CompaniesIncluded + c.NoSnapshot + c.SnapshotNotAtInstant + c.NoSignalsInWindow + c.CompanyWithUnresolvableLink);
        Assert.Equal(10, report.Rows.Count);
    }

    [Fact]
    public async Task TheNotednessDiscount_FeedsTheRecomposition_SoATieredNotedCompanyStillRecomposes()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("MEG", 80, 90, [HighFiling, HighNews, MediumInsider], tier: FollowingTier.Mega);
        u.AddCompany("SML", 80, 10, [HighFiling, HighNews, MediumInsider], tier: FollowingTier.Small);

        var report = await u.BuildAsync();

        Assert.Equal(0, report.Counts.OpportunityRecompositionMismatch);
        Assert.Equal(2, report.Counts.CompaniesRanked);
        Assert.True(Row(report, "SML").Opportunity > Row(report, "MEG").Opportunity);
        Assert.Equal(FollowingTier.Mega, Row(report, "MEG").FollowingTier);
    }

    // ---- the spec-225 follow-up: which signal sets bestConfidence, and which term carries the spread ----

    private const string CollectorEnvelope = """{"metadata":{"collector":"sec-edgar"},"companyHints":[]}""";

    /// <summary>An AI directional earnings read over a High filing (inferred producer), at <paramref name="confidence"/>.</summary>
    private static TestSignalSpec AiRead(decimal confidence, SignalDirection direction = SignalDirection.Positive) =>
        new(EvidenceSourceType.Filing, EvidenceQuality.High, confidence,
            Type: SignalType.GuidanceChange, Direction: direction,
            Reason: "Revenue grew and guidance was raised.", EvidenceMetadataJson: CollectorEnvelope,
            Title: "8-K (2026-08-03) [items: 2.02] Results of Operations", Strength: 8);

    /// <summary>A keyword phrase-rule signal over a High filing at <paramref name="confidence"/>.</summary>
    private static TestSignalSpec KeywordFiling(decimal confidence, SignalType type = SignalType.StrategicPartnership) =>
        new(EvidenceSourceType.Filing, EvidenceQuality.High, confidence,
            Type: type, Reason: KeywordSignalReasons.MatchedPhrase("material definitive agreement"));

    /// <summary>The keyword news MediaAttention event over High news at 0.5.</summary>
    private static readonly TestSignalSpec NewsEvent =
        new(EvidenceSourceType.NewsArticle, EvidenceQuality.High, 0.5m,
            Type: SignalType.MediaAttention, Direction: SignalDirection.Neutral, Reason: KeywordSignalReasons.MediaAttention);

    /// <summary>
    /// Four companies where ONLY bestConfidence varies (every company: one High filing signal + one High news
    /// event ⇒ quality 0.85 and diversity 2/3 everywhere). Attention 0, Small tier ⇒ discount 1.0.
    /// A1: AI read 0.95 ⇒ EC 80, Trajectory 50 ⇒ Opportunity 40;
    /// A2: AI read 0.90 ⇒ EC 76, Trajectory 55 ⇒ 42;
    /// K1: keyword 0.50 ⇒ EC 42, Trajectory 80 ⇒ 34;
    /// K2: keyword 0.55 ⇒ EC 47, Trajectory 85 ⇒ 40.
    /// Median EC 61.5 ⇒ A1 and A2 are above it, both from the AI read.
    /// </summary>
    private static EvidenceConfidenceTestUniverse OnlyBestConfidenceVaries()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("A1", 50, 0, [AiRead(0.95m), NewsEvent]);
        u.AddCompany("A2", 55, 0, [AiRead(0.90m), NewsEvent]);
        u.AddCompany("K1", 80, 0, [KeywordFiling(0.50m), NewsEvent]);
        u.AddCompany("K2", 85, 0, [KeywordFiling(0.55m, SignalType.CapitalRaise), NewsEvent]);
        return u;
    }

    [Fact]
    public async Task TheBestConfidenceSignal_IsRecordedPerCompany_WithTypeDirectionProducerCollectorAndTitle()
    {
        var report = await OnlyBestConfidenceVaries().BuildAsync();

        var a1 = Row(report, "A1");
        Assert.Equal((80, 0.95), (a1.EvidenceConfidencePersisted, a1.BestConfidence));
        Assert.Equal(SignalType.GuidanceChange, a1.BestConfidenceSignalType);
        Assert.Equal(SignalDirection.Positive, a1.BestConfidenceSignalDirection);
        Assert.Equal(8, a1.BestConfidenceSignalStrength);
        Assert.Equal(SignalProducer.AiEarningsReadDirectional, a1.BestConfidenceProducer);
        Assert.Equal(EvidenceSourceType.Filing, a1.BestConfidenceEvidenceSourceType);
        Assert.Equal("sec-edgar", a1.BestConfidenceCollector);
        Assert.Equal("8-K (2026-08-03) [items: 2.02] Results of Operations", a1.BestConfidenceEvidenceTitle);
        Assert.False(a1.BestConfidenceComparabilityCapNoted);
        Assert.Equal(1, a1.BestConfidenceTieCount);
        Assert.Equal(["GuidanceChange"], a1.BestConfidenceTiedSignalTypes!);

        var k2 = Row(report, "K2");
        Assert.Equal(SignalType.CapitalRaise, k2.BestConfidenceSignalType);
        Assert.Equal(SignalProducer.KeywordPhraseRule, k2.BestConfidenceProducer);
        // Nothing was stamped on that evidence: NOT RECORDED, never a guessed collector.
        Assert.Null(k2.BestConfidenceCollector);

        var b = report.BestSignal;
        Assert.Equal(SignalProducerRule.Version, b.ProducerRuleVersion);
        Assert.Equal(4, b.CompaniesWithBestSignal);
        Assert.Equal(0, b.UnclassifiedProducer);
        Assert.Equal(
            [(0.95, SignalType.GuidanceChange, SignalProducer.AiEarningsReadDirectional),
             (0.9, SignalType.GuidanceChange, SignalProducer.AiEarningsReadDirectional),
             (0.55, SignalType.CapitalRaise, SignalProducer.KeywordPhraseRule),
             // K1's 0.5 keyword filing signal TIES with its 0.5 news event; the tie-break (earliest observed)
             // reports the news event, and the tie is counted rather than hidden.
             (0.5, SignalType.MediaAttention, SignalProducer.KeywordMediaAttention)],
            b.ByValueTypeProducer.Select(s => (s.BestConfidence, s.SignalType, s.Producer)).ToArray());
        Assert.All(b.ByValueTypeProducer, s => Assert.Equal(0.25, s.Share));
        Assert.Equal(2, Row(report, "K1").BestConfidenceTieCount);
        Assert.Equal(1, b.TiesSpanningProducers);
    }

    [Fact]
    public async Task TiesAtTheBestConfidence_AreCounted_AndATieAcrossTypesOrProducersIsNamed()
    {
        var u = new EvidenceConfidenceTestUniverse();
        // K1's keyword filing signal ties with its 0.5 news event: two types AND two producers at the maximum.
        // Signals are observed newest-first by index, so the tie-break (earliest ObservedAtUtc) picks the LAST.
        u.AddCompany("TIE", 80, 0, [KeywordFiling(0.50m), NewsEvent]);
        u.AddCompany("ONE", 50, 0, [AiRead(0.95m), NewsEvent]);

        var report = await u.BuildAsync();

        var tie = Row(report, "TIE");
        Assert.Equal(2, tie.BestConfidenceTieCount);
        Assert.Equal(SignalType.MediaAttention, tie.BestConfidenceSignalType);
        Assert.Equal(["MediaAttention", "StrategicPartnership"], tie.BestConfidenceTiedSignalTypes!);
        Assert.Equal(["KeywordMediaAttention", "KeywordPhraseRule"], tie.BestConfidenceTiedProducers!);

        Assert.Equal(1, report.BestSignal.CompaniesTiedAtBestConfidence);
        Assert.Equal(1, report.BestSignal.TiesSpanningSignalTypes);
        Assert.Equal(1, report.BestSignal.TiesSpanningProducers);
    }

    [Fact]
    public async Task ACappedRead_IsNotedOnTheRow_AndCountedInItsCell()
    {
        var u = new EvidenceConfidenceTestUniverse();
        u.AddCompany("CAP", 60, 0, [AiRead(0.65m) with { Reason = "Quarter." + FilingReadSignalMetadata.ComparabilityCapReasonMarker + "'impairment')" }]);
        u.AddCompany("UNC", 60, 0, [AiRead(0.65m)]);

        var report = await u.BuildAsync();

        Assert.True(Row(report, "CAP").BestConfidenceComparabilityCapNoted);
        Assert.False(Row(report, "UNC").BestConfidenceComparabilityCapNoted);
        var cell = Assert.Single(report.BestSignal.ByValueTypeProducer);
        Assert.Equal((0.65, 2, 1), (cell.BestConfidence, cell.Companies, cell.ComparabilityCapNoted));
    }

    [Fact]
    public async Task AnExcludedCompany_RecordsNoBestConfidenceSignal()
    {
        var u = OnlyBestConfidenceVaries();
        u.AddCompany("NIL", 0, 0, []);
        u.AddCompanyWithoutSnapshot("NEW");

        var report = await u.BuildAsync();

        foreach (var ticker in new[] { "NIL", "NEW" })
        {
            var row = Row(report, ticker);
            Assert.Null(row.BestConfidenceSignalId);
            Assert.Null(row.BestConfidenceSignalType);
            Assert.Null(row.BestConfidenceProducer);
            Assert.Null(row.BestConfidenceTieCount);
            Assert.Null(row.BestConfidenceComparabilityCapNoted);
        }

        Assert.Equal(4, report.BestSignal.CompaniesWithBestSignal);
    }

    [Fact]
    public async Task WhenOnlyBestConfidenceVaries_ItCarriesTheWholeLogVariance_AndHoldingTheOthersMovesNothing()
    {
        var report = await OnlyBestConfidenceVaries().BuildAsync();

        var a = report.TermAttribution;
        Assert.Null(a.LogVarianceUndefinedReason);
        Assert.Null(a.HeldTermNotAvailableReason);
        Assert.Equal(4, a.TermRowCount);
        Assert.Equal(4, a.HeldTermRankedCount);
        Assert.Equal(EvidenceConfidenceDistributionReporter.TermBestConfidence, a.DominantTerm);
        Assert.Equal(1.0, a.DominantTermLogVarianceShare!.Value, 12);

        var conf = a.Terms.Single(t => t.Term == EvidenceConfidenceDistributionReporter.TermBestConfidence);
        var qual = a.Terms.Single(t => t.Term == EvidenceConfidenceDistributionReporter.TermBestQualityWeight);
        var div = a.Terms.Single(t => t.Term == EvidenceConfidenceDistributionReporter.TermDiversityFactor);
        Assert.Equal(1.0, a.Terms.Sum(t => t.LogVarianceShare!.Value), 12);
        Assert.Equal(0.0, qual.LogVarianceShare!.Value, 12);
        Assert.Equal(0.0, div.LogVarianceShare!.Value, 12);

        // ρ(EC, bestConfidence) is a perfect monotone; a constant term is undefined and says which side.
        Assert.Equal(1.0, conf.RhoWithEvidenceConfidence!.Value, 12);
        Assert.Null(qual.RhoWithEvidenceConfidence);
        Assert.Equal("undefined: constant series (bestQualityWeight)", qual.RhoUndefinedReason);

        // Holding a constant term at its own median changes no score and no rank.
        Assert.Equal((0, 0, 0), (qual.EvidenceConfidenceChangedWhenHeld, qual.RankChangedWhenHeld, qual.MaxAbsRankDeltaWhenHeld));
        Assert.Equal((0, 0), (div.EvidenceConfidenceChangedWhenHeld, div.RankChangedWhenHeld));

        // Holding bestConfidence at its median (0.55 + 0.90) / 2 = 0.725 ⇒ EC 61 for everyone ⇒ Opportunity
        // A1 31, A2 34, K1 49, K2 52 ⇒ ranks K2, K1, A2, A1 against actual A2, K2, A1, K1: all four move.
        Assert.Equal(0.725, conf.Median!.Value, 12);
        Assert.Equal(4, conf.EvidenceConfidenceChangedWhenHeld);
        Assert.Equal(4, conf.RankChangedWhenHeld);
        Assert.Equal(1.0, conf.RankChangedShareWhenHeld);
        Assert.Equal(2, conf.MaxAbsRankDeltaWhenHeld);
    }

    [Fact]
    public async Task TheAboveMedianProfile_NamesTheModalTypeAndProducer_AndTheVerdictReadsTheEvidenceTypeAxis()
    {
        var report = await OnlyBestConfidenceVaries().BuildAsync();

        var m = report.AboveMedian;
        Assert.Equal(61.5, m.MedianEvidenceConfidence);
        Assert.Equal(2, m.Companies);
        Assert.Equal(("GuidanceChange", 2, 1.0), (m.ModalSignalType, m.ModalSignalTypeCompanies, m.ModalSignalTypeShare));
        Assert.Equal((nameof(SignalProducer.AiEarningsReadDirectional), 2), (m.ModalProducer, m.ModalProducerCompanies));
        Assert.Equal([("Positive", 2)], m.ByDirection.Select(c => (c.Category, c.Companies)).ToArray());

        // Not flat (every rank moves; four distinct EC values), no collector-count signal (distinct source types
        // are constant ⇒ ρ undefined), but bestConfidence carries the spread and the AI read carries bestConfidence.
        var v = report.Verdict;
        Assert.Equal("evidence-confidence-verdict-v2", v.RuleVersion);
        Assert.Equal(EvidenceConfidenceVerdict.DiscriminatesTheWrongThing, v.Verdict);
        Assert.Equal([EvidenceConfidenceVerdictRule.AxisEvidenceType], v.WrongThingAxes);
        Assert.Null(v.RhoEvidenceConfidenceVsDistinctSourceTypes);
        Assert.Equal(report.Correlations.Single(c => c.Pair == "EvidenceConfidence vs Trajectory").Rho, v.RhoEvidenceConfidenceVsTrajectory);
        Assert.Contains("the AI earnings read", v.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonV8Formula_StillAttributesTheLogVariance_ButHasNoHeldTermRanking()
    {
        var u = OnlyBestConfidenceVaries();
        u.Strategies = new ScoringStrategySet(
        [
            new ScoringStrategyDefinition("default", "default", new ScoringWeights(), IsPrimary: true)
            {
                Formula = ScoreFormulaVersions.V11,
                Channels = ScoringChannelSet.Create(
                    [ScoringChannel.Collector("filings", ["sec-edgar"], 1.0, 3)], "default"),
            },
        ]);

        var report = await u.BuildAsync();

        Assert.Equal(EvidenceConfidenceDistributionReporter.TermBestConfidence, report.TermAttribution.DominantTerm);
        Assert.Contains("not available", report.TermAttribution.HeldTermNotAvailableReason, StringComparison.Ordinal);
        Assert.All(report.TermAttribution.Terms, t => Assert.Null(t.RankChangedWhenHeld));
        Assert.Equal(0, report.TermAttribution.HeldTermRankedCount);
    }

    [Fact]
    public async Task TheSummaryLine_IsLoggedOncePerBuild_AtInformation()
    {
        var logger = new CapturingReporterLogger();

        await FourCompanies().BuildAsync(logger: logger);

        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(EvidenceConfidenceDistributionReporter.ArtifactVersion, line.Message, StringComparison.Ordinal);
        Assert.Contains("4 seeded, 4 included, 4 ranked", line.Message, StringComparison.Ordinal);
        Assert.Contains("dominant term bestConfidence", line.Message, StringComparison.Ordinal);
        Assert.Contains("verdict DiscriminatesTheWrongThing", line.Message, StringComparison.Ordinal);
    }
}
