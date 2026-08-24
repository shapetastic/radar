namespace Radar.Application.NewsRisk.Judgment;

/// <summary>
/// The CLOSED factual-trajectory vocabulary (spec 185 §2) — the ONE balance axis v1 carries beside its
/// challenge-only findings. Judged against the FIXED rubric ("the company's recent business trajectory"),
/// never a per-company thesis. There is no ThesisSupported verdict and no support score in v1:
/// <c>Improving</c> with zero findings IS the supportive read, expressed factually.
/// </summary>
public enum NewsJudgmentTrajectory
{
    /// <summary>
    /// The degraded state, and DELIBERATELY the zero value (the spec-182 convention, applied by spec 186):
    /// a record that somehow hydrates as the default must never read as the BEST state. Persistence is
    /// token-only everywhere (the shared file-store JSON options render enums as names and reject integer
    /// values; the wire shape is a string parsed by <see cref="Radar.Application.NewsTyping.NewsTypingTokens"/>,
    /// which rejects digits), so the member order carries no persisted or wire meaning.
    /// </summary>
    Unknown = 0,
    Improving,
    Deteriorating,
    Mixed,
}

/// <summary>
/// The WIRE shape of the judge's structured response (spec 185 §2) — deliberately all strings/numbers (the
/// spec-179 rule), so an out-of-vocabulary value arrives as data the validator can NAME in a drop reason
/// instead of being silently coerced by enum deserialization. Nothing here is persisted as-is: only the
/// validated projection is. The shape carries NO Radar score, rank or label member — enforced structurally
/// by the judgment architecture guard test.
/// </summary>
public sealed record NewsJudgmentModelResponse(
    string? BusinessTrajectory,
    int? ChallengeStrength,
    IReadOnlyList<NewsJudgmentModelFinding>? Findings,
    string? Rationale);

/// <summary>
/// One raw model finding: spec-179 risk-taxonomy category/severity tokens, confidence, the supporting
/// FactIds (which must exist in the supplied set), and the attribution caveat the prompt obliges whenever
/// every supporting fact sits below <c>reported</c> assertion status.
/// </summary>
public sealed record NewsJudgmentModelFinding(
    string? Category,
    string? Severity,
    double? Confidence,
    IReadOnlyList<string>? FactIds,
    string? AttributionCaveat);

/// <summary>
/// A validated finding: typed category/severity (the spec-179 vocabularies, REUSED not copied), confidence
/// in [0,1], at least one supplied FactId, and — when every supporting fact is below <c>reported</c> — a
/// non-blank attribution caveat. Family size appears NOWHERE here: findings cite FactIds, and nothing in
/// validation reads MemberCount, so syndication volume cannot multiply findings.
/// </summary>
public sealed record NewsJudgmentValidatedFinding(
    NewsRiskCategory Category,
    NewsRiskSeverity Severity,
    double Confidence,
    IReadOnlyList<Guid> FactIds,
    string? AttributionCaveat);
