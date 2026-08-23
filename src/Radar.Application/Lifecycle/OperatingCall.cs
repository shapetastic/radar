namespace Radar.Application.Lifecycle;

/// <summary>
/// The CLOSED per-strategy operating-call vocabulary (spec 184 §2). A call is a declared, journaled,
/// falsifiable DECISION about reader-facing prominence — never an efficacy result, never a score input.
/// Exactly one Research arm is <see cref="Lead"/> unless an explicit (or fallback) StopAll is in force.
/// </summary>
public enum OperatingCall
{
    /// <summary>The one arm that governs ALL user-facing narrative and action-label prominence.</summary>
    Lead = 0,

    /// <summary>A research arm still running; resolved by supersession (promoted or stopped later).</summary>
    Trial = 1,

    /// <summary>An arm deliberately kept OFF the front page, with its basis stated.</summary>
    DoNotLead = 2,

    /// <summary>A stopped arm — fully visible in the diagnostic appendix, never hidden.</summary>
    Stop = 3,
}

/// <summary>Who made a call: a human, or the gate-default rule (GatePassed → Lead, GateFailed → Stop).</summary>
public enum OperatingCallActor
{
    /// <summary>Token <c>human</c>.</summary>
    Human = 0,

    /// <summary>Token <c>gate-default</c>.</summary>
    GateDefault = 1,
}

/// <summary>How a resolved call turned out, judged by the immutable resolution rule declared with it.</summary>
public enum OperatingCallOutcome
{
    /// <summary>The declared rule has not yet been able to judge the call.</summary>
    Unresolved = 0,

    /// <summary>The declared rule judged the call right.</summary>
    Right = 1,

    /// <summary>The declared rule judged the call wrong — recorded, with an evidence reference.</summary>
    Wrong = 2,
}
