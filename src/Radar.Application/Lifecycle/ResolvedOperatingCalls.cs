namespace Radar.Application.Lifecycle;

/// <summary>Where a strategy's EFFECTIVE call came from after reduction (spec 184 §2).</summary>
public enum ResolvedCallProvenance
{
    /// <summary>The file's declared call applied verbatim.</summary>
    DeclaredCall = 0,

    /// <summary>A persisted gate verdict applied its default (GatePassed → Lead, GateFailed → Stop).</summary>
    GateDefault = 1,

    /// <summary>
    /// A configured Research arm with no declared call and no gate verdict is treated as a Trial — an arm
    /// still running whose call has simply not been made. Rendered as such ("no declared call"), never
    /// silently merged with a declared Trial.
    /// </summary>
    ImplicitTrial = 2,
}

/// <summary>
/// A declared gate override that did NOT bind to the artifact's current verdict (spec 186 §3): the call
/// named <paramref name="BoundVerdictId"/>, the artifact now carries <paramref name="CurrentVerdictId"/>
/// (empty when the artifact carries no verdict identity at all — a pre-186 artifact, or no verdict), so the
/// gate default re-armed. Reported, never silently dropped: new evidence SHOULD re-open a call, and the
/// maintainer must be able to see exactly why and re-declare against the current id.
/// </summary>
public sealed record StaleGateOverride(
    string StrategyName, string BoundVerdictId, string CurrentVerdictId);

/// <summary>One Research arm's effective call after reduction, with full provenance.</summary>
/// <param name="StrategyName">The configured strategy name.</param>
/// <param name="Call">The effective call.</param>
/// <param name="Provenance">How the effective call was arrived at.</param>
/// <param name="Declared">The file's declared call for this arm, when one exists (kept even when a gate
/// default overrode it, so the report can state both).</param>
/// <param name="GateVerdict">The persisted gate verdict that applied, when one did.</param>
/// <param name="StaleOverride">
/// Set when this arm's call declared <c>overridesGate</c> but did not bind to the current verdict id
/// (spec 186 §3). Trailing + defaulted, so every pre-186 construction is unchanged.
/// </param>
public sealed record ResolvedStrategyCall(
    string StrategyName,
    OperatingCall Call,
    ResolvedCallProvenance Provenance,
    StrategyOperatingCall? Declared,
    StrategyGateVerdict? GateVerdict,
    StaleGateOverride? StaleOverride = null);

/// <summary>
/// The output of the ONE deterministic reducer (spec 184 §2): every Research arm's effective call, and the
/// single global answer — exactly one Lead, or StopAll (declared, or the predeclared zero-Lead fallback).
/// </summary>
public sealed record ResolvedOperatingCalls
{
    private ResolvedOperatingCalls(
        bool hasDeclaredCalls,
        string? undeclaredReason,
        bool stopAll,
        string? stopAllReason,
        string? leadStrategyName,
        IReadOnlyList<ResolvedStrategyCall> calls)
    {
        HasDeclaredCalls = hasDeclaredCalls;
        UndeclaredReason = undeclaredReason;
        StopAll = stopAll;
        StopAllReason = stopAllReason;
        LeadStrategyName = leadStrategyName;
        Calls = calls;
    }

    /// <summary>False when no operating-calls file exists: the call layer is undeclared, prominence stays
    /// with the storage primary BY DEFAULT, and the report says so explicitly.</summary>
    public bool HasDeclaredCalls { get; }

    /// <summary>Why no calls are in force, when <see cref="HasDeclaredCalls"/> is false.</summary>
    public string? UndeclaredReason { get; }

    /// <summary>True when no Lead exists — declared <c>globalCall: StopAll</c> or the fallback.</summary>
    public bool StopAll { get; }

    /// <summary>Why StopAll is in force ("declared", or the zero-Lead fallback explanation).</summary>
    public string? StopAllReason { get; }

    /// <summary>The single Lead arm. Null exactly when <see cref="StopAll"/> or no calls are declared.</summary>
    public string? LeadStrategyName { get; }

    /// <summary>Every Research arm's effective call, in the configured strategy order (AD-3).</summary>
    public IReadOnlyList<ResolvedStrategyCall> Calls { get; }

    /// <summary>
    /// Every declared gate override that did not bind to the artifact's current verdict id (spec 186 §3),
    /// in the configured strategy order (AD-3). Derived from <see cref="Calls"/> — there is exactly one
    /// place a stale override is decided, and this cannot disagree with it. Empty in the overwhelmingly
    /// normal case, which is what keeps the rendered report byte-identical when nothing is stale.
    /// </summary>
    public IReadOnlyList<StaleGateOverride> StaleOverrides =>
        [.. Calls.Where(c => c.StaleOverride is not null).Select(c => c.StaleOverride!)];

    /// <summary>The effective call for one arm, or null (e.g. a Comparator, which can carry no call).</summary>
    public ResolvedStrategyCall? For(string strategyName) =>
        Calls.FirstOrDefault(c =>
            string.Equals(c.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase));

    public static ResolvedOperatingCalls None(string undeclaredReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(undeclaredReason);
        return new(
            hasDeclaredCalls: false,
            undeclaredReason: undeclaredReason,
            stopAll: false,
            stopAllReason: null,
            leadStrategyName: null,
            calls: []);
    }

    internal static ResolvedOperatingCalls WithLead(
        string leadStrategyName, IReadOnlyList<ResolvedStrategyCall> calls) =>
        new(
            hasDeclaredCalls: true,
            undeclaredReason: null,
            stopAll: false,
            stopAllReason: null,
            leadStrategyName: leadStrategyName,
            calls: calls);

    internal static ResolvedOperatingCalls Stopped(
        string stopAllReason, IReadOnlyList<ResolvedStrategyCall> calls) =>
        new(
            hasDeclaredCalls: true,
            undeclaredReason: null,
            stopAll: true,
            stopAllReason: stopAllReason,
            leadStrategyName: null,
            calls: calls);
}
