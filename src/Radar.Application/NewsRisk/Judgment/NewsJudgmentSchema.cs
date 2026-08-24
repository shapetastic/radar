using Radar.Application.NewsTyping;

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
    string? Rationale,
    // Spec 187 §1: the SUPPLIED FactIds the model says actually ESTABLISH BusinessTrajectory — not every
    // fact it read. Strings on the wire (the all-strings rule), so an unparseable or unsupplied id arrives
    // as data the validator NAMES instead of being coerced. Optional/trailing on the WIRE only, so a model
    // that omits the field produces a named validation failure rather than a deserialization exception; a
    // valid v2 response always carries it, and it is EMPTY iff the trajectory is Unknown.
    IReadOnlyList<string>? TrajectoryFactIds = null);

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

/// <summary>
/// Spec 187 §1 — the CONTEXT-ONLY event types: facts that describe OTHER PEOPLE'S VIEWS
/// (<see cref="NewsEventType.AnalystOrRatingAction"/>), PRICE/TRADING BEHAVIOUR
/// (<see cref="NewsEventType.MarketReaction"/>, <see cref="NewsEventType.IndexOrTradingMechanics"/>) or
/// CONTENT MECHANICS (<see cref="NewsEventType.PromotionalOrListicle"/>). They are legitimate news and are
/// never discarded, but on their own they establish nothing about a company's recent BUSINESS trajectory —
/// the first live judged run turned a YORW 52-week share-price low into a high-confidence
/// business-execution finding, which is exactly this class of error.
/// <para>
/// Defined ONCE and consumed by BOTH validator rules — the trajectory-evidence gate and the finding-level
/// <c>non-business-context-only</c> drop. The Infrastructure prompt is NOT rendered from this list, and
/// deliberately so: rule (5) states a BROADER context class in prose, additionally naming institutional
/// holdings/trades and conference attendance, which taxonomy v1 carries no token for (spec 187 §1's KGS
/// note). The two are therefore not one rendered list and cannot be. What IS mechanically enforced is the
/// weaker, achievable claim: every member must declare the prompt wording that names it
/// (<see cref="PromptPhrases"/>), and a test asserts each declared phrase actually appears in the judge's
/// system instruction. Adding a member here therefore REQUIRES declaring its prompt phrase, and a prompt
/// edit whenever that wording is not already present — which then forks
/// <c>NewsJudgmentContract.PromptVersion</c> and moves the pinned instruction hash. A future taxonomy member
/// whose wording rule (5) ALREADY carries (it names institutional holdings/trades and conference attendance
/// today) can be declared against the existing text with no prompt edit and no hash move; that is the
/// guard working as specified, not a hole in it — the obligation is to say WHICH words cover the member,
/// not to invent new ones.
/// </para>
/// <para>
/// A family is context-only iff it declares at least one event type and EVERY declared type is a member:
/// one other type is enough to make it a business fact. An empty type list is deliberately NOT treated as
/// context-only — "we cannot tell" must not read as "we can reject".
/// </para>
/// </summary>
public static class NewsJudgmentContextOnlyEventTypes
{
    /// <summary>The closed context-only set, in taxonomy declaration order (AD-3).</summary>
    public static readonly IReadOnlyList<NewsEventType> Members =
    [
        NewsEventType.AnalystOrRatingAction,
        NewsEventType.MarketReaction,
        NewsEventType.IndexOrTradingMechanics,
        NewsEventType.PromotionalOrListicle,
    ];

    /// <summary>
    /// The PROMPT WORDING that names each member inside the judge's rule (5). No member is named by its
    /// taxonomy token in the prompt — the instruction speaks plain English to the model — so this table is
    /// the declared bridge between the two, and a test asserts every phrase is genuinely present in the
    /// instruction text. It is deliberately a per-member DECLARATION rather than a rendering: the prompt's
    /// context class is broader than the taxonomy (see the type remarks), so adding a member obliges the
    /// maintainer to say, here, which prompt words cover it — and to write those words if they are absent.
    /// Keyed by every member of <see cref="Members"/>, no more and no less (asserted).
    /// </summary>
    public static readonly IReadOnlyDictionary<NewsEventType, string> PromptPhrases =
        new Dictionary<NewsEventType, string>
        {
            [NewsEventType.AnalystOrRatingAction] = "analyst targets or ratings",
            [NewsEventType.MarketReaction] = "share-price moves",
            [NewsEventType.IndexOrTradingMechanics] = "index changes",
            [NewsEventType.PromotionalOrListicle] = "promotional or listicle coverage",
        };

    private static readonly HashSet<NewsEventType> Set = [.. Members];

    /// <summary>Whether one event type is context-only.</summary>
    public static bool Contains(NewsEventType type) => Set.Contains(type);

    /// <summary>
    /// Whether a supplied family is CONFINED to context-only event types (declares at least one type and
    /// every declared type is context-only).
    /// </summary>
    public static bool IsConfinedTo(IReadOnlyList<NewsEventType> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        return eventTypes.Count > 0 && eventTypes.All(Set.Contains);
    }
}
