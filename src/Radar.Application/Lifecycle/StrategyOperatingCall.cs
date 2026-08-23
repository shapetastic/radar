namespace Radar.Application.Lifecycle;

/// <summary>
/// One declared operating call from <c>data/strategy-operating-calls.json</c> (spec 184 §2). The call is a
/// falsifiable decision: <see cref="ResolutionRule"/> is IMMUTABLE text fixed at call time, and the call is
/// later resolved Right/Wrong/Unresolved against that rule — never against a rule invented afterwards.
/// </summary>
/// <param name="Strategy">The configured strategy the call is about (must be a Research arm).</param>
/// <param name="Call">The declared call.</param>
/// <param name="AsOfUtc">When the call was made (UTC). Used by the reducer's predates-the-verdict rule.</param>
/// <param name="Basis">Why the call was made, stated at call time.</param>
/// <param name="Actor">Who made it (<c>human</c> or <c>gate-default</c>).</param>
/// <param name="OverridesGate">
/// True only for a deliberate human override of a persisted gate verdict. Default false: absent an explicit
/// override, the gate default always wins over the file's call (spec 184 §2 reducer rule 1).
/// </param>
/// <param name="ReviewByUtc">The exact UTC review checkpoint — a review, not a resolution.</param>
/// <param name="ResolutionRule">
/// The immutable rule the call resolves by, declared with the call. May be null for a call that has not
/// declared one, but a <see cref="Resolution"/> can never exist without it (validated).
/// </param>
/// <param name="Resolution">The recorded resolution, when the declared rule has judged the call.</param>
public sealed record StrategyOperatingCall(
    string Strategy,
    OperatingCall Call,
    DateTimeOffset AsOfUtc,
    string Basis,
    OperatingCallActor Actor,
    bool OverridesGate,
    DateTimeOffset ReviewByUtc,
    string? ResolutionRule,
    OperatingCallResolution? Resolution);

/// <summary>
/// A recorded resolution of a call: the outcome under the call's own immutable rule, when it was resolved,
/// and the evidence reference the judgement rests on (evidence before opinions — a "Wrong" without evidence
/// is not a record, it is an assertion).
/// </summary>
public sealed record OperatingCallResolution(
    OperatingCallOutcome Outcome,
    DateTimeOffset ResolvedAtUtc,
    string EvidenceRef);
