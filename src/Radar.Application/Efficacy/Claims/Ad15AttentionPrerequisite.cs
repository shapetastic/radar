namespace Radar.Application.Efficacy.Claims;

/// <summary>
/// The neutral DTO carrying AD-16's screen outcome into the AD-15 claim gate (spec 170). Produced by the
/// composition that can see both sides (the Worker, via the Attention module's mapper); consumed by the
/// comparison generator — which therefore never references an Attention type.
/// <para>
/// The invariant "<see cref="WasCalculated"/> is true exactly when the outcome is <see cref="Ad16ScreenOutcome.Miss"/>
/// or <see cref="Ad16ScreenOutcome.ClearsNecessaryScreen"/>" is enforced STRUCTURALLY (private constructor +
/// factory), not by convention — a prerequisite that reads as calculated while carrying a Pending outcome
/// would be the fail-open shape this slice exists to close, arriving inside the fix.
/// </para>
/// </summary>
public sealed record Ad15AttentionPrerequisite
{
    private Ad15AttentionPrerequisite(bool wasCalculated, Ad16ScreenOutcome outcome)
    {
        WasCalculated = wasCalculated;
        Outcome = outcome;
    }

    /// <summary>
    /// Whether AD-16's screen was actually CALCULATED — i.e. produced a <see cref="Ad16ScreenOutcome.Miss"/>
    /// or <see cref="Ad16ScreenOutcome.ClearsNecessaryScreen"/> over its minimum eligible dates. This is what
    /// AD-15's suspension requires; a passing outcome is NOT required (Miss satisfies the prerequisite too,
    /// and the renderer states the Miss beside any licensed claim).
    /// </summary>
    public bool WasCalculated { get; }

    /// <summary>The screen outcome, in the closed neutral vocabulary.</summary>
    public Ad16ScreenOutcome Outcome { get; }

    /// <summary>The absent prerequisite: nobody ran the screen. Can never satisfy the gate.</summary>
    public static Ad15AttentionPrerequisite NotCalculated { get; } =
        new(wasCalculated: false, Ad16ScreenOutcome.NotCalculated);

    /// <summary>
    /// Builds a prerequisite for <paramref name="outcome"/>, deriving <see cref="WasCalculated"/> from it. A
    /// value outside the defined enum members is coerced to <see cref="Ad16ScreenOutcome.Invalid"/> — the
    /// state machine is total, and an unrecognised state fails CLOSED rather than falling through.
    /// </summary>
    public static Ad15AttentionPrerequisite For(Ad16ScreenOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            return new Ad15AttentionPrerequisite(wasCalculated: false, Ad16ScreenOutcome.Invalid);
        }

        var calculated = outcome is Ad16ScreenOutcome.Miss or Ad16ScreenOutcome.ClearsNecessaryScreen;
        return new Ad15AttentionPrerequisite(calculated, outcome);
    }
}
