using Radar.Application.Efficacy.Claims;

namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// Maps an <see cref="AttentionArrivalScreenResult"/> onto the neutral
/// <see cref="Ad15AttentionPrerequisite"/> the AD-15 claim gate consumes (spec 170 §1.1). It lives on the
/// ATTENTION side of the boundary — Attention → Claims is permitted; Comparison → Attention is not — and the
/// Worker performs the mapping through it before invoking the comparison generator.
/// <para>
/// <b>The state machine is TOTAL, and its invalid state is named.</b> <c>Availability == Available</c> with a
/// null or unrecognised <c>ScreenStatus</c> is representable even though the evaluator does not intend it —
/// it maps to <see cref="Ad16ScreenOutcome.Invalid"/>, which does NOT satisfy the prerequisite. Letting it
/// fall through to a Pending-like or satisfied branch would be the fail-open shape spec 170 exists to close,
/// arriving inside the fix. An unrecognised <c>Availability</c> is equally uninterpretable and maps to
/// <see cref="Ad16ScreenOutcome.Invalid"/> too.
/// </para>
/// </summary>
public static class Ad15AttentionPrerequisiteMap
{
    public static Ad15AttentionPrerequisite From(AttentionArrivalScreenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Availability switch
        {
            // A configuration failure is never accrual and never a calculated screen — regardless of any
            // status the record might (contract-violatingly) carry beside it.
            AttentionEvaluationAvailability.Unavailable =>
                Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.Unavailable),

            AttentionEvaluationAvailability.Available => result.ScreenStatus switch
            {
                AttentionScreenStatus.Pending => Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.Pending),
                AttentionScreenStatus.Miss => Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.Miss),
                AttentionScreenStatus.ClearsNecessaryScreen =>
                    Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.ClearsNecessaryScreen),
                // Null or an unrecognised status under an Available evaluation: uninterpretable, fails closed.
                _ => Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.Invalid),
            },

            // An availability value this mapper has never heard of cannot be interpreted either way.
            _ => Ad15AttentionPrerequisite.For(Ad16ScreenOutcome.Invalid),
        };
    }
}
