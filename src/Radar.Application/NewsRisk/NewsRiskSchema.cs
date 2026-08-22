namespace Radar.Application.NewsRisk;

/// <summary>
/// The CLOSED top-level assessment vocabulary (spec 179 §5). Closed on purpose: the evaluator and the live
/// artifact consume these as durable facts, and a free-form verdict would let outcomes appear that nothing
/// downstream understands. "No risk found" is a statement about the SUPPLIED text only — the live artifact's
/// caveat states that absence of a detected risk is not evidence a company is safe.
/// </summary>
public enum NewsRiskAssessmentKind
{
    /// <summary>The supplied text supports at least one validated financing/dilution/solvency/execution/credibility risk claim.</summary>
    ThesisChallenged = 0,

    /// <summary>The supplied text was sufficient and supports no such risk. Renders ONLY under the fail-closed §7 gate.</summary>
    NoRiskFoundInSuppliedText,

    /// <summary>The supplied text was too thin/ambiguous to assess. NOT a low risk score.</summary>
    InsufficientContent,
}

/// <summary>The CLOSED risk-category vocabulary (spec 179 §5) — exactly the eleven values, nothing free-form.</summary>
public enum NewsRiskCategory
{
    LiquidityOrGoingConcern = 0,
    DilutionOrFinancingDependence,
    DebtOrCovenant,
    DelistingOrReverseSplit,
    ExecutionOrMissedMilestone,
    GuidanceCredibility,
    UnitEconomicsOrMargin,
    RegulatoryOrLegalSetback,
    CustomerOrRevenueConcentration,
    GovernanceOrRelatedParty,
    OtherSpecifiedRisk,
}

/// <summary>Claim severity — a closed three-level ordinal, validated (never free text).</summary>
public enum NewsRiskSeverity
{
    Low = 0,
    Medium,
    High,
}

/// <summary>
/// The WIRE shape of the model's structured response (spec 179 §5) — deliberately all strings/numbers, so an
/// out-of-vocabulary value arrives as data the validator can NAME in a drop reason instead of being silently
/// coerced by enum deserialization. Nothing here is persisted as-is: only the §6-validated projection is.
/// </summary>
public sealed record NewsRiskModelResponse(
    string? Assessment,
    int? RiskScore,
    IReadOnlyList<string>? Categories,
    IReadOnlyList<NewsRiskModelClaim>? Claims,
    string? Rationale);

/// <summary>One raw model claim: category/severity tokens, confidence, cited observation ids and verbatim excerpts.</summary>
public sealed record NewsRiskModelClaim(
    string? Category,
    string? Severity,
    double? Confidence,
    IReadOnlyList<string>? ObservationIds,
    IReadOnlyList<string>? Excerpts);

/// <summary>A §6-validated claim: typed category/severity, confidence in [0,1], and only excerpts that are exact ordinal substrings of supplied text.</summary>
public sealed record NewsRiskValidatedClaim(
    NewsRiskCategory Category,
    NewsRiskSeverity Severity,
    double Confidence,
    IReadOnlyList<Guid> ObservationIds,
    IReadOnlyList<string> Excerpts);
