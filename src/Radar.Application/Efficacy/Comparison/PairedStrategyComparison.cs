using Radar.Application.Efficacy.Claims;
using Radar.Application.Efficacy.Statistics;

namespace Radar.Application.Efficacy.Comparison;

/// <summary>
/// Why one candidate as-of date was excluded from the paired family. Machine-readable — the spec-155 mirror
/// of the leaderboard's <see cref="StrategyDropReason"/> pattern: a dropped date is a RESULT FIELD with a
/// stable reason, never a log line.
/// </summary>
public enum PairedDateDropReason
{
    /// <summary>Fewer joint companies on the date than <c>MinimumCompaniesPerDate</c>.</summary>
    TooFewCompanies,

    /// <summary>The primary's scores are constant across the date's joint companies — no rank variance.</summary>
    ConstantPrimary,

    /// <summary>The shared outcome vector is constant across the date's joint companies.</summary>
    ConstantOutcome,

    /// <summary>
    /// A baseline's scores are constant across the date's joint companies. The date drops for the WHOLE
    /// family — every baseline's delta must use the same dates — and the offending baseline is named on the
    /// dropped-date record.
    /// </summary>
    ConstantBaseline,

    /// <summary>
    /// The date's nominal outcome window <c>(d, d+h]</c> overlaps an earlier admitted block's, so the purge
    /// skipped it. Counted, never silently discarded.
    /// </summary>
    OverlappingOutcomeWindow,
}

/// <summary>How much support a set of observations has — the (observations, companies, dates) triple.</summary>
public sealed record PairedSupport(int Observations, int DistinctCompanies, int DistinctAsOfDates)
{
    public static PairedSupport Empty { get; } = new(0, 0, 0);
}

/// <summary>One strategy's marginal (own-series) support, with the spec-152 exclusion tallies.</summary>
public sealed record StrategyMarginalSupport(
    string StrategyName,
    PairedSupport Support,
    int ObservationsWithoutForwardPrice,
    int ObservationsWithPartialWindow);

/// <summary>
/// The primary-vs-one-baseline pairwise intersection support — DISCLOSED as a diagnostic only. The claim path
/// uses exclusively the joint intersection, because pairwise intersections could each answer over a different
/// period or company set and "beats every baseline" must be one question, not several.
/// </summary>
public sealed record PairwiseIntersectionSupport(string BaselineName, PairedSupport Support);

/// <summary>One dropped candidate date; <paramref name="BaselineName"/> only for <c>ConstantBaseline</c>.</summary>
public sealed record PairedDroppedDate(DateOnly Date, PairedDateDropReason Reason, string? BaselineName);

/// <summary>One baseline's per-date coefficient and its paired delta against the primary.</summary>
public sealed record PairedBaselineRho(string BaselineName, double Rho, double Delta);

/// <summary>
/// One surviving candidate date: the joint company count and every arm's cross-sectional Spearman ρ against
/// the SAME outcome ranks. Per-date ρ is a coefficient, not a claim — no interval is attached here.
/// </summary>
public sealed record PairedCandidateDate(
    DateOnly Date,
    int Companies,
    double PrimaryRho,
    IReadOnlyList<PairedBaselineRho> Baselines);

/// <summary>
/// One purge-admitted block with its OBSERVED price interval (earliest entry bar, latest exit bar across the
/// block's joint companies) — disclosed so the non-overlap of consecutive admitted blocks is verifiable from
/// the result rather than trusted.
/// </summary>
public sealed record PairedAdmittedBlock(DateOnly Date, DateOnly ObservedEntry, DateOnly ObservedExit);

/// <summary>One admitted block's paired delta for one baseline: <c>ρ_primary(d) − ρ_baseline(d)</c>.</summary>
public sealed record PairedDelta(DateOnly Date, double Delta);

/// <summary>
/// One baseline's headline: the purged median paired delta, its exact two-sided 95% order-statistic interval,
/// and the sign-test diagnostic — all over the SAME admitted blocks.
/// </summary>
/// <param name="MedianDelta">The exact median of the admitted deltas; <c>null</c> when no block was admitted.</param>
/// <param name="ClearsGate">
/// Whether THIS baseline satisfies its share of the PRICE half of the AD-15 gate: interval defined, median
/// strictly positive, interval lower bound strictly positive. The overall price gate additionally needs the
/// boundary and predeclared primary — see <see cref="PairedStrategyComparison.SatisfiesPriceGate"/> — and
/// the COMPOSITE gate additionally needs AD-16's attention prerequisite (spec 170, judged by
/// <c>Ad15ClaimGate</c> outside this harness).
/// </param>
public sealed record BaselinePairedResult(
    string BaselineName,
    IReadOnlyList<PairedDelta> AdmittedDeltas,
    double? MedianDelta,
    ExactMedianIntervalResult Interval,
    SignTestResult SignTest,
    bool ClearsGate);

/// <summary>
/// The full result of the spec-155 paired, date-blocked, purged strategy comparison — every disclosure the
/// amended AD-15 requires is a FIELD here, so a renderer cannot omit one and a caller cannot re-derive a
/// friendlier gate: <see cref="SatisfiesPriceGate"/> is computed inside the harness.
/// <para>
/// <b>The price half is NOT the claim (spec 170).</b> AD-15's gate is COMPOSITE: this record carries only its
/// PRICE half; AD-16's attention prerequisite is judged by <c>Ad15ClaimGate</c> in <c>Efficacy.Claims</c>,
/// which composes <see cref="SatisfiesPriceGate"/> + <see cref="PriceGateReasons"/> with the prerequisite
/// into the one verdict that can license a claim. The rename from <c>QualifiesUnderAd15</c> is deliberate: a
/// price-side result must be unable to READ as the claim even when this record is consumed directly.
/// </para>
/// <para>
/// This is a research statistic about Radar's own scoring, never a recommendation about a company and never
/// advice (AD-9). "A materially smaller intersection is a result, not a log message" — hence the marginal,
/// pairwise and joint supports all being fields.
/// </para>
/// </summary>
/// <param name="PrimaryStrategyName">The arm the deltas are measured FROM.</param>
/// <param name="PrimaryWasPredeclared">
/// Whether the primary was named in configuration BEFORE this evaluation (the AD-15 precondition). When
/// false the primary defaulted to the pipeline's primary strategy and the result is exploratory.
/// </param>
/// <param name="FirstEligibleAsOf">The precommitted claim boundary; <c>null</c> ⇒ exploratory.</param>
/// <param name="ArmsConsidered">
/// The selection disclosure: how many strategies were handed in — a primary chosen among many arms needs a
/// separately accepted multiplicity rule before any non-predeclared arm may claim anything.
/// </param>
/// <param name="JointSupport">
/// The ALL-HISTORY joint intersection across the primary and every baseline. It describes the DATASET, never
/// the claim — see <paramref name="EligibleJointSupport"/> for the claim's support (spec 170 §3).
/// </param>
/// <param name="EligibleJointSupport">
/// The joint intersection restricted to as-of dates at or after the precommitted boundary — the support the
/// claim actually rests on. With no boundary it is EMPTY (never the all-history figure): no boundary means no
/// claim path at all.
/// </param>
/// <param name="InconsistentOutcomeObservationsDropped">
/// Joint observations whose forward outcome DISAGREED across arms — structurally impossible while every arm
/// shares one price store, so any non-zero count is a data defect surfaced, never a choice of which outcome
/// to keep.
/// </param>
/// <param name="ObservationsWithoutAsOfInstant">
/// UNIT: OBSERVATIONS (de-duped company-days). Usable observations across the primary and every baseline
/// that were excluded from the claim path because their snapshot carried no <c>AsOfInstantUtc</c> — they
/// fail CLOSED, never falling back to date pairing, since a legacy point is exactly the case where the two
/// arms' knowledge cutoffs are unverifiable (spec 170 §2.3).
/// </param>
/// <param name="ObservationsWithMismatchedAsOfInstant">
/// UNIT: KEYS. Count of (company, calendar-date) keys present in two or more arms whose instants differed
/// with no instant common to every arm carrying the key — therefore NOT paired. The partial-rerun signature:
/// two arms scored the same company-day from different knowledge cutoffs (spec 170 §2.3).
/// </param>
/// <param name="DevelopmentDateCount">
/// Surviving candidate dates BEFORE the boundary: reported as development data, never in the claim interval.
/// </param>
/// <param name="SatisfiesPriceGate">
/// The PRICE half of the composite AD-15 gate only (renamed from <c>QualifiesUnderAd15</c>, spec 170): true
/// exactly when <paramref name="PriceGateReasons"/> is empty. NEVER sufficient for a claim on its own.
/// </param>
/// <param name="PriceGateReasons">
/// Every reason the PRICE half did not pass, as structured closed-coded records
/// (<see cref="Ad15GateReason"/>); empty exactly when <see cref="SatisfiesPriceGate"/> is true. The
/// composite verdict appends the AD-16 prerequisite reasons — see <c>Ad15ClaimGate</c>.
/// </param>
public sealed record PairedStrategyComparison(
    string PrimaryStrategyName,
    bool PrimaryWasPredeclared,
    DateOnly? FirstEligibleAsOf,
    int ArmsConsidered,
    IReadOnlyList<string> BaselineNames,
    IReadOnlyList<StrategyMarginalSupport> MarginalSupports,
    IReadOnlyList<PairwiseIntersectionSupport> PairwiseSupports,
    PairedSupport JointSupport,
    PairedSupport EligibleJointSupport,
    int InconsistentOutcomeObservationsDropped,
    int ObservationsWithoutAsOfInstant,
    int ObservationsWithMismatchedAsOfInstant,
    IReadOnlyList<PairedCandidateDate> CandidateDates,
    IReadOnlyList<PairedDroppedDate> DroppedDates,
    int DevelopmentDateCount,
    IReadOnlyList<PairedAdmittedBlock> AdmittedBlocks,
    IReadOnlyList<BaselinePairedResult> Baselines,
    bool SatisfiesPriceGate,
    IReadOnlyList<Ad15GateReason> PriceGateReasons,
    PairedComparisonOptions Options);
