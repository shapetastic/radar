namespace Radar.Application.Efficacy.Attention;

/// <summary>Whether the evaluation's prerequisites held at all.</summary>
public enum AttentionEvaluationAvailability
{
    /// <summary>Every prerequisite held; <see cref="AttentionArrivalScreenResult.ScreenStatus"/> is populated.</summary>
    Available = 0,

    /// <summary>A prerequisite failed. The screen status is NULL — a configuration failure is never reported as accrual.</summary>
    Unavailable,
}

/// <summary>
/// Why the evaluation could not run. Each is a CONFIGURATION or CAPABILITY failure, deliberately distinct
/// from <see cref="AttentionScreenStatus.Pending"/>, which means "the data has not accrued yet".
/// </summary>
public enum AttentionEvaluationUnavailableReason
{
    /// <summary>Not unavailable.</summary>
    None = 0,

    /// <summary>The exclusion cohort could not be loaded, or contradicts the watch universe. Silently including all companies would violate an accepted AD-16 amendment.</summary>
    CohortConfigurationUnavailable,

    /// <summary>An enabled collector other than the supported one can emit third-party <c>MediaAttention</c> but supplies no coverage contract.</summary>
    UnsupportedAttentionCollector,

    /// <summary>AD-16 §7's primary arm is not among the configured strategies, so there is nothing to screen.</summary>
    PrimaryStrategyNotConfigured,
}

/// <summary>AD-16 §7's three screen tokens. Used verbatim in JSON; rendered as restrained prose in Markdown.</summary>
public enum AttentionScreenStatus
{
    /// <summary>Fewer than 20 eligible dates. Expected accrual, not a defect and not a result.</summary>
    Pending = 0,

    /// <summary>At least 20 eligible dates and median δ &lt;= 0. A MISS at the declared horizon; it may not be rescued by changing the outcome or horizon after inspection.</summary>
    Miss,

    /// <summary>At least 20 eligible dates and median δ &gt; 0. Clears this NECESSARY screen only — never proof of efficacy.</summary>
    ClearsNecessaryScreen,
}

/// <summary>Why a candidate as-of date does not count toward AD-16's minimum of 20 eligible dates.</summary>
public enum AttentionDateExclusionReason
{
    /// <summary>Not excluded.</summary>
    None = 0,

    /// <summary>Earlier than AD-16 §4's precommitted first eligible date.</summary>
    BeforeFirstEligibleDate,

    /// <summary>Fewer than 20 companies survived the cohort exclusion and the per-company requirements.</summary>
    InsufficientCompanies,

    /// <summary>Every company's forward publisher count is identical: no rank variance to correlate against.</summary>
    ConstantOutcome,

    /// <summary>Every company's primary-arm score is identical.</summary>
    ConstantPrimaryPredictor,

    /// <summary>Every company's trailing publisher count is identical.</summary>
    ConstantPersistencePredictor,
}

/// <summary>Why one company does not enter a candidate date's eligible set.</summary>
public enum AttentionCompanyExclusionReason
{
    /// <summary>Not excluded.</summary>
    None = 0,

    /// <summary>A member of a cohort declared <c>excludeFromPrimaryScreen</c>. Applied BEFORE the minimum-N count (AD-16, 2026-07-31).</summary>
    EventEnrichedCohort,

    /// <summary>No primary-arm snapshot exists at this exact as-of instant.</summary>
    NoPrimarySnapshot,

    /// <summary>Collection coverage over the comparator and/or outcome window could not be proved (AD-16 §5).</summary>
    IncompleteAttentionCollection,

    /// <summary>A relevant comparator signal's evidence did not resolve.</summary>
    UnresolvedComparatorEvidence,

    /// <summary>A relevant outcome signal's evidence did not resolve.</summary>
    UnresolvedOutcomeEvidence,

    /// <summary>A comparator article carried no real third-party publisher.</summary>
    MissingComparatorPublisher,

    /// <summary>An outcome article carried no real third-party publisher.</summary>
    MissingOutcomePublisher,

    /// <summary>A comparator article's collector attribution was missing or unsupported.</summary>
    UnresolvedComparatorProvenance,

    /// <summary>An outcome article's collector attribution was missing or unsupported.</summary>
    UnresolvedOutcomeProvenance,
}

/// <summary>
/// A reported ρ, or the named reason it is undefined. A diagnostic is ALWAYS rendered — never NaN, never a
/// fabricated 0 — because "we could not compute this" and "this is zero" are different statements.
/// </summary>
public sealed record AttentionDiagnostic(string Name, bool IsDefined, double Rho, string UndefinedReason)
{
    public static AttentionDiagnostic Defined(string name, double rho) =>
        new(name, IsDefined: true, Rho: rho, UndefinedReason: string.Empty);

    public static AttentionDiagnostic Undefined(string name, string reason) =>
        new(name, IsDefined: false, Rho: 0.0, UndefinedReason: reason);
}

/// <summary>One company's observation on one eligible as-of date. Every number is read off a stored snapshot or a counted publisher set.</summary>
public sealed record AttentionCompanyObservation(
    Guid CompanyId,
    string Ticker,
    int PrimaryOpportunityScore,
    int AttentionScore,
    int ComparatorPublishers,
    int OutcomePublishers);

/// <summary>One company's exclusion from a candidate date, with the specific coverage sub-reason when there is one.</summary>
public sealed record AttentionCompanyExclusion(
    Guid CompanyId,
    string Ticker,
    AttentionCompanyExclusionReason Reason,
    AttentionCoverageReason CoverageReason,
    AttentionCheckpointDisqualification CoverageDetail);

/// <summary>A stable exclusion-token count. A LIST, not a dictionary, so the rendered order is fixed (AD-3).</summary>
public sealed record AttentionExclusionCount(string Reason, int Count);

/// <summary>
/// One candidate as-of date: its exact instant, whether it is eligible, the support behind it, the primary
/// statistic and every reported diagnostic.
/// </summary>
public sealed record AttentionArrivalDateRow(
    DateOnly AsOfDateUtc,
    DateTimeOffset AsOfInstantUtc,
    bool IsEligible,
    AttentionDateExclusionReason ExclusionReason,
    int CompaniesConsidered,
    int CompaniesInExcludedCohort,
    int CompaniesIncluded,
    IReadOnlyList<AttentionExclusionCount> ExclusionCounts,
    AttentionDiagnostic PrimaryCorrelation,
    AttentionDiagnostic PersistenceCorrelation,
    bool IsDeltaDefined,
    double Delta,
    AttentionDiagnostic SecondaryAttentionScoreCorrelation,
    AttentionDiagnostic ControlCorrelation,
    bool IsPrimaryMinusControlDefined,
    double PrimaryMinusControl,
    IReadOnlyList<AttentionDiagnostic> BaselineCorrelations,
    IReadOnlyList<AttentionCompanyObservation> Observations,
    IReadOnlyList<AttentionCompanyExclusion> Exclusions);

/// <summary>
/// One labelled section of the evaluation: the binding PRIMARY screen, or the separately-reported EXPLORATORY
/// event-enriched cohort. The exploratory section is produced by the SAME builders over a disjoint company
/// set; it can never satisfy the primary minimum N and never changes the primary status.
/// </summary>
public sealed record AttentionArrivalSection(
    string Label,
    bool IsPrimary,
    int CandidateDates,
    int EligibleDates,
    bool IsMedianDeltaDefined,
    double MedianDelta,
    IReadOnlyList<AttentionArrivalDateRow> Dates);

/// <summary>
/// The whole AD-16 attention-arrival evaluation (spec 169). Read-only: it creates, amends and deletes no
/// score, signal, evidence or review, and it promotes nothing.
/// <para>
/// <see cref="ScreenStatus"/> is populated ONLY when <see cref="Availability"/> is
/// <see cref="AttentionEvaluationAvailability.Available"/>. A configuration failure leaves it null under its
/// own named reason: mislabelling a broken prerequisite as <c>Pending</c> would quietly promise a result that
/// can never arrive.
/// </para>
/// <para>
/// The daily windows OVERLAP and are not independent, so this result carries no confidence or significance
/// claim in either direction. Spec 155's purged interval is the later confirmatory layer; the per-date rows
/// here are preserved for it.
/// </para>
/// </summary>
public sealed record AttentionArrivalScreenResult(
    AttentionEvaluationAvailability Availability,
    AttentionEvaluationUnavailableReason UnavailableReason,
    string? UnavailableDetail,
    AttentionScreenStatus? ScreenStatus,
    DateOnly FirstEligibleAsOfDateUtc,
    int HorizonDays,
    int MinimumCompaniesPerDate,
    int MinimumEligibleDates,
    string PrimaryStrategy,
    string ControlStrategy,
    IReadOnlyList<string> BaselineStrategies,
    string AttentionCollector,
    AttentionArrivalSection Primary,
    AttentionArrivalSection Exploratory)
{
    /// <summary>An evaluation whose prerequisites failed: no status, both sections empty, the reason named.</summary>
    public static AttentionArrivalScreenResult Unavailable(
        AttentionEvaluationUnavailableReason reason, string detail, string attentionCollector) =>
        new(
            Availability: AttentionEvaluationAvailability.Unavailable,
            UnavailableReason: reason,
            UnavailableDetail: detail,
            ScreenStatus: null,
            FirstEligibleAsOfDateUtc: AttentionArrivalScreen.FirstEligibleAsOfDateUtc,
            HorizonDays: (int)AttentionArrivalScreen.Horizon.TotalDays,
            MinimumCompaniesPerDate: AttentionArrivalScreen.MinimumCompaniesPerDate,
            MinimumEligibleDates: AttentionArrivalScreen.MinimumEligibleDates,
            PrimaryStrategy: AttentionArrivalScreen.PrimaryStrategyName,
            ControlStrategy: AttentionArrivalScreen.ControlStrategyName,
            BaselineStrategies: AttentionArrivalScreen.BaselineStrategyNames,
            AttentionCollector: attentionCollector,
            Primary: EmptySection(AttentionArrivalSections.Primary, isPrimary: true),
            Exploratory: EmptySection(AttentionArrivalSections.Exploratory, isPrimary: false));

    internal static AttentionArrivalSection EmptySection(string label, bool isPrimary) =>
        new(
            Label: label,
            IsPrimary: isPrimary,
            CandidateDates: 0,
            EligibleDates: 0,
            IsMedianDeltaDefined: false,
            MedianDelta: 0.0,
            Dates: []);
}

/// <summary>The two section labels, so the renderers and the evaluator cannot disagree about them.</summary>
public static class AttentionArrivalSections
{
    public const string Primary = "primary";

    public const string Exploratory = "exploratory-event-enriched";
}
