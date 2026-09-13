using Radar.Domain.Companies;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Efficacy.EvidenceConfidence;

/// <summary>Why a seeded company is, or is not, in the measured set. Every non-<see cref="Included"/> state is a counted axis.</summary>
public enum EvidenceConfidenceCompanyState
{
    /// <summary>A snapshot at the measurement instant with at least one resolvable stored link; terms recorded.</summary>
    Included = 0,

    /// <summary>The strategy's store holds no snapshot for this company at all.</summary>
    NoSnapshot,

    /// <summary>The company's LATEST snapshot ends before the measurement instant (an older window), so it is not the same read.</summary>
    SnapshotNotAtInstant,

    /// <summary>
    /// The snapshot at the instant carries ZERO stored links: the formula scored an empty window and persisted
    /// an all-zero component set. That 0 is a DEFAULTED zero, so every component here is NOT RECORDED (null).
    /// </summary>
    NoSignalsInWindow,

    /// <summary>At least one stored link's signal or evidence could not be resolved, so the scored set cannot be rebuilt.</summary>
    CompanyWithUnresolvableLink,
}

/// <summary>The spec-225 §3 answer, as a closed set. <see cref="NotDetermined"/> carries its reason on the section.</summary>
public enum EvidenceConfidenceVerdict
{
    /// <summary>The counterfactual was not available (non-v8 formula, or fewer than two ranked companies).</summary>
    NotDetermined = 0,

    /// <summary>(a) Values spread and ranks move when the component is held constant.</summary>
    Discriminates,

    /// <summary>(b) Near-constant across the universe, or holding it constant moves few ranks: a flat tax.</summary>
    FlatTax,

    /// <summary>
    /// (c) It varies, but along an axis other than how well-evidenced the thesis is: how many collectors fired
    /// (distinct source types), and/or — since <c>evidence-confidence-verdict-v2</c> — which TYPE of signal is a
    /// company's strongest (one term carries the spread and one signal type carries that term). The section's
    /// <c>WrongThingAxes</c> names which.
    /// </summary>
    DiscriminatesTheWrongThing,
}

/// <summary>One distinct value, how many included companies hold it, and their share of the INCLUDED count.</summary>
public sealed record EvidenceConfidenceValueShare<T>(T Value, int Companies, double Share);

/// <summary>A Spearman rank correlation between two per-company series over the included set, or why it is undefined.</summary>
public sealed record EvidenceConfidenceCorrelation(
    string Pair,
    int N,
    double? Rho,
    string? UndefinedReason);

/// <summary>
/// The counted axes. Always present, <c>0</c> when nothing hit. The identity that must hold:
/// <c>CompaniesSeeded == CompaniesIncluded + NoSnapshot + SnapshotNotAtInstant + NoSignalsInWindow +
/// CompanyWithUnresolvableLink</c>. <see cref="LinkSignalUnresolvable"/> / <see cref="LinkEvidenceUnresolvable"/>
/// count LINKS, not companies.
/// </summary>
public sealed record EvidenceConfidenceCounts(
    int CompaniesSeeded,
    int CompaniesIncluded,
    int CompaniesRanked,
    int NoSnapshot,
    int SnapshotNotAtInstant,
    int NoSignalsInWindow,
    int LinkSignalUnresolvable,
    int LinkEvidenceUnresolvable,
    int CompanyWithUnresolvableLink,
    int TermsDisagreeWithSnapshot,
    int OpportunityRecompositionMismatch,
    int FormulaDoesNotComposeOpportunityFromEvidenceConfidence,
    int MixedScoringConfigVersion)
{
    public static EvidenceConfidenceCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The reconciliation the artifact and its test both assert.</summary>
    public bool Reconciles =>
        CompaniesSeeded
            == CompaniesIncluded + NoSnapshot + SnapshotNotAtInstant + NoSignalsInWindow
                + CompanyWithUnresolvableLink;
}

/// <summary>
/// The live distribution of the persisted component and of each term it multiplies. Shares are of the INCLUDED
/// count; the term tables exclude rows whose recomputed terms disagree with the persisted score.
/// </summary>
public sealed record EvidenceConfidenceDistributions(
    int IncludedCount,
    int TermRowCount,
    IReadOnlyList<EvidenceConfidenceValueShare<int>> EvidenceConfidence,
    int DistinctEvidenceConfidenceValues,
    int? ModalEvidenceConfidence,
    double? ModalEvidenceConfidenceShare,
    double? MedianEvidenceConfidence,
    IReadOnlyList<EvidenceConfidenceValueShare<double>> BestConfidence,
    IReadOnlyList<EvidenceConfidenceValueShare<double>> BestQualityWeight,
    IReadOnlyList<EvidenceConfidenceValueShare<int>> DistinctSourceTypes,
    IReadOnlyList<EvidenceConfidenceValueShare<double>> DiversityFactor)
{
    public static EvidenceConfidenceDistributions Empty { get; } =
        new(0, 0, [], 0, null, null, null, [], [], [], []);
}

/// <summary>
/// The computed-but-NOT-applied counterfactual: every ranked company's Opportunity recomposed with
/// EvidenceConfidence held at the universe median, and how the ranking moves.
/// </summary>
public sealed record EvidenceConfidenceCounterfactual(
    bool Available,
    string? NotAvailableReason,
    int? HeldEvidenceConfidence,
    string HeldValueRule,
    string RankingRule,
    int CompaniesRanked,
    int? RankChanged,
    double? RankChangedShare,
    int? MaxAbsRankDelta,
    string? MaxAbsRankDeltaCompany,
    int TopN,
    int? EnterTopN,
    int? LeaveTopN,
    double? MedianActualOpportunity,
    double? MedianCounterfactualOpportunity)
{
    public static EvidenceConfidenceCounterfactual NotAvailable(string reason) =>
        new(
            Available: false,
            NotAvailableReason: reason,
            HeldEvidenceConfidence: null,
            HeldValueRule: EvidenceConfidenceDistributionReporter.HeldValueRule,
            RankingRule: EvidenceConfidenceDistributionReporter.RankingRule,
            CompaniesRanked: 0,
            RankChanged: null,
            RankChangedShare: null,
            MaxAbsRankDelta: null,
            MaxAbsRankDeltaCompany: null,
            TopN: EvidenceConfidenceDistributionReporter.TopN,
            EnterTopN: null,
            LeaveTopN: null,
            MedianActualOpportunity: null,
            MedianCounterfactualOpportunity: null);
}

/// <summary>
/// One (best-confidence value, signal type, producer) cell of the best-confidence table: how many term-row
/// companies take their <c>bestConfidence</c> from a signal of that type and producer at that value, their share
/// of the term rows, and how many of those signals carry the comparability-cap annotation.
/// </summary>
public sealed record EvidenceConfidenceBestSignalShare(
    double BestConfidence,
    SignalType SignalType,
    SignalProducer Producer,
    int Companies,
    double Share,
    int ComparabilityCapNoted);

/// <summary>A named category, its company count and its share of the stated denominator.</summary>
public sealed record EvidenceConfidenceCategoryCount(string Category, int Companies, double Share);

/// <summary>
/// WHICH signal sets each company's <c>bestConfidence</c> (the spec-225 follow-up). Over the term rows only
/// (recomputed terms agree with the persisted score). The reported signal is chosen by <see cref="TieBreakRule"/>
/// when several share the maximum; every tie is counted, and so is a tie that spans more than one signal type or
/// producer — the cases where the attribution to one type is a convention rather than a fact.
/// </summary>
public sealed record EvidenceConfidenceBestSignalProfile(
    string ProducerRuleVersion,
    string TieBreakRule,
    int CompaniesWithBestSignal,
    IReadOnlyList<EvidenceConfidenceBestSignalShare> ByValueTypeProducer,
    IReadOnlyList<EvidenceConfidenceCategoryCount> ByProducerAndDirection,
    int CompaniesTiedAtBestConfidence,
    int TiesSpanningSignalTypes,
    int TiesSpanningProducers,
    int UnclassifiedProducer)
{
    public static EvidenceConfidenceBestSignalProfile Empty { get; } =
        new(SignalProducerRule.Version, EvidenceConfidenceDistributionReporter.BestSignalTieBreakRule, 0, [], [], 0, 0, 0, 0);
}

/// <summary>
/// The above-median companies' best-confidence signals: is the upper half of EvidenceConfidence dominated by ONE
/// signal type / producer? Shares are of <see cref="Companies"/>. The modal category is the largest count, ties
/// broken by the category name (ordinal) — stated so it is reproducible.
/// </summary>
public sealed record EvidenceConfidenceAboveMedianProfile(
    string Rule,
    double? MedianEvidenceConfidence,
    int Companies,
    IReadOnlyList<EvidenceConfidenceCategoryCount> BySignalType,
    IReadOnlyList<EvidenceConfidenceCategoryCount> ByProducer,
    IReadOnlyList<EvidenceConfidenceCategoryCount> ByDirection,
    string? ModalSignalType,
    int? ModalSignalTypeCompanies,
    double? ModalSignalTypeShare,
    string? ModalProducer,
    int? ModalProducerCompanies,
    double? ModalProducerShare)
{
    public static EvidenceConfidenceAboveMedianProfile Empty(string rule) =>
        new(rule, null, 0, [], [], [], null, null, null, null, null, null);
}

/// <summary>
/// One term's attribution: its median, ρ(persisted EvidenceConfidence, term), its share of the variance of
/// ln(EvidenceConfidence product), and the Opportunity ranking under "this term alone held at its median, the
/// other two as measured" (computed through the production composition, NOT applied).
/// </summary>
public sealed record EvidenceConfidenceTermAttributionRow(
    string Term,
    double? Median,
    double? RhoWithEvidenceConfidence,
    string? RhoUndefinedReason,
    double? LogVarianceShare,
    int? EvidenceConfidenceChangedWhenHeld,
    int? RankChangedWhenHeld,
    double? RankChangedShareWhenHeld,
    int? MaxAbsRankDeltaWhenHeld);

/// <summary>
/// Which of the three terms the spread of EvidenceConfidence comes from (the spec-225 follow-up). The log-variance
/// shares are an exact covariance decomposition of ln(product) = Σ ln(factor) — they sum to 1 — computed on the
/// UNCLAMPED, unrounded product; the held-term counterfactuals are over the companies that are BOTH term rows and
/// ranked. <see cref="DominantTerm"/> is the term with the largest log-variance share (a measured fact, not a rule).
/// </summary>
public sealed record EvidenceConfidenceTermAttribution(
    int TermRowCount,
    int HeldTermRankedCount,
    string LogVarianceRule,
    string? LogVarianceUndefinedReason,
    string HeldTermRule,
    string? HeldTermNotAvailableReason,
    IReadOnlyList<EvidenceConfidenceTermAttributionRow> Terms,
    string? DominantTerm,
    double? DominantTermLogVarianceShare)
{
    public static EvidenceConfidenceTermAttribution Empty(string reason) =>
        new(0, 0, EvidenceConfidenceDistributionReporter.LogVarianceRule, reason,
            EvidenceConfidenceDistributionReporter.HeldTermRule, reason, [], null, null);
}

/// <summary>
/// The verdict beside its inputs AND the rule's thresholds. The thresholds are the RULE's choices
/// (<see cref="EvidenceConfidenceVerdictRule.Version"/>), printed so a reader can disagree with them — they are
/// not measured facts. <see cref="WrongThingAxes"/> names every (c) axis that fired (empty unless the verdict is
/// (c)): <c>CollectorCount</c> (tracks distinct source types) and/or <c>EvidenceType</c> (one term carries the
/// spread and one signal type carries that term in the upper half).
/// </summary>
public sealed record EvidenceConfidenceVerdictSection(
    string RuleVersion,
    EvidenceConfidenceVerdict Verdict,
    string Sentence,
    string? NotDeterminedReason,
    double? RankChangedShare,
    double? ModalShare,
    double? RhoEvidenceConfidenceVsDistinctSourceTypes,
    double RankChangedShareAtOrBelowIsFlatTax,
    double ModalShareAtOrAboveIsFlatTax,
    double AbsRhoDistinctSourceTypesAtOrAboveIsWrongThing,
    IReadOnlyList<string> WrongThingAxes,
    string? DominantTerm,
    double? DominantTermLogVarianceShare,
    int AboveMedianCompanies,
    string? AboveMedianModalSignalType,
    int? AboveMedianModalSignalTypeCompanies,
    double? AboveMedianModalSignalTypeShare,
    string? AboveMedianModalProducer,
    int? AboveMedianModalProducerCompanies,
    double? RhoEvidenceConfidenceVsTrajectory,
    double DominantTermLogVarianceShareAtOrAboveIsSingleTerm,
    double AboveMedianModalSignalTypeShareAtOrAboveIsSingleType);

/// <summary>One seeded company's row. A <c>null</c> is NOT RECORDED — never a measured zero.</summary>
public sealed record EvidenceConfidenceCompanyRow(
    Guid CompanyId,
    string? Ticker,
    string Name,
    FollowingTier FollowingTier,
    EvidenceConfidenceCompanyState State,
    DateTimeOffset? SnapshotWindowEndUtc,
    string? ScoringConfigVersion,
    int LinkCount,
    int LinkSignalUnresolvable,
    int LinkEvidenceUnresolvable,
    int? EvidenceConfidencePersisted,
    int? EvidenceConfidenceRecomputed,
    bool TermsDisagreeWithSnapshot,
    double? BestConfidence,
    double? BestQualityWeight,
    int? DistinctSourceTypes,
    double? DiversityFactor,
    int? Trajectory,
    int? Attention,
    int? Opportunity,
    bool OpportunityRecompositionMismatch,
    int? CounterfactualOpportunity,
    int? ActualRank,
    int? CounterfactualRank,
    int? RankDelta,
    IReadOnlyList<string> Flags,
    Guid? BestConfidenceSignalId = null,
    SignalType? BestConfidenceSignalType = null,
    SignalDirection? BestConfidenceSignalDirection = null,
    int? BestConfidenceSignalStrength = null,
    SignalProducer? BestConfidenceProducer = null,
    EvidenceSourceType? BestConfidenceEvidenceSourceType = null,
    string? BestConfidenceCollector = null,
    string? BestConfidenceEvidenceTitle = null,
    DateTimeOffset? BestConfidenceObservedAtUtc = null,
    bool? BestConfidenceComparabilityCapNoted = null,
    int? BestConfidenceTieCount = null,
    IReadOnlyList<string>? BestConfidenceTiedSignalTypes = null,
    IReadOnlyList<string>? BestConfidenceTiedProducers = null);

/// <summary>
/// The spec-225 artifact (<c>evidence-confidence-distribution-v1</c>): a measurement over persisted snapshots +
/// stored links at ONE instant, for ONE strategy. It changes no score.
/// </summary>
public sealed record EvidenceConfidenceDistributionReport(
    string ArtifactVersion,
    string VerdictRuleVersion,
    bool StrategyConfigured,
    string? StrategyNotConfiguredReason,
    string? StrategyName,
    string? Formula,
    bool FormulaIsV8,
    string? ScoringProfile,
    IReadOnlyDictionary<string, double> EvidenceConfidenceWeights,
    string SeriesDescription,
    DateTimeOffset? InstantUtc,
    DateTimeOffset? WindowStartUtc,
    IReadOnlyList<string> ScoringConfigVersionsSeen,
    EvidenceConfidenceCounts Counts,
    EvidenceConfidenceDistributions Distributions,
    IReadOnlyList<EvidenceConfidenceCorrelation> Correlations,
    EvidenceConfidenceCounterfactual Counterfactual,
    EvidenceConfidenceTermAttribution TermAttribution,
    EvidenceConfidenceBestSignalProfile BestSignal,
    EvidenceConfidenceAboveMedianProfile AboveMedian,
    EvidenceConfidenceVerdictSection Verdict,
    IReadOnlyList<EvidenceConfidenceCompanyRow> Rows);
