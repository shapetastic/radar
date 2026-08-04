namespace Radar.Application.Efficacy.Claims;

/// <summary>
/// The NEUTRAL projection of AD-16's attention-arrival screen outcome (spec 170). This enum lives in
/// <c>Efficacy.Claims</c> — a namespace both <c>Efficacy.Attention</c> and <c>Efficacy.Comparison</c> may
/// depend on — precisely so the comparison can consume the prerequisite without referencing an Attention type
/// (Comparison → Attention stays forbidden and guardrail-tested; Attention → Claims and Comparison → Claims
/// are both permitted).
/// <para>
/// The vocabulary is CLOSED and total: every state the screen can be in — including "nobody ran it" and "the
/// result could not be interpreted" — has its own member, so the AD-15 gate never has to guess what an absent
/// or unreadable prerequisite means. Only <see cref="Miss"/> and <see cref="ClearsNecessaryScreen"/> satisfy
/// the prerequisite: AD-15 requires the screen to be CALCULATED, not passed.
/// </para>
/// </summary>
public enum Ad16ScreenOutcome
{
    /// <summary>
    /// No screen result was supplied at all — the attention generator is disabled or was never run. The
    /// default (0) DELIBERATELY, so even a <c>default</c>-constructed value fails closed.
    /// </summary>
    NotCalculated = 0,

    /// <summary>The screen could not be evaluated — a configuration/capability failure, per AD-16.</summary>
    Unavailable,

    /// <summary>The screen ran but its minimum eligible dates have not accrued. Not yet calculated in AD-15's sense.</summary>
    Pending,

    /// <summary>The screen WAS calculated and returned a Miss at its declared horizon. Satisfies the prerequisite.</summary>
    Miss,

    /// <summary>The screen WAS calculated and cleared its necessary screen (never proof of efficacy). Satisfies the prerequisite.</summary>
    ClearsNecessaryScreen,

    /// <summary>
    /// The screen result could not be interpreted — e.g. an Available evaluation carrying a null or
    /// unrecognised status. Named rather than folded into a <see cref="Pending"/>-like state, because an
    /// unreadable prerequisite reading as "just wait" is the exact fail-open shape spec 170 exists to close.
    /// Does NOT satisfy the prerequisite.
    /// </summary>
    Invalid,
}
