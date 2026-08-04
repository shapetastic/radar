namespace Radar.Application.Efficacy.Claims;

/// <summary>
/// The COMPOSITE AD-15 claim gate (spec 170): composes the price-side result computed by the paired harness
/// with AD-16's attention prerequisite into one <see cref="Ad15ClaimVerdict"/>. Pure and deterministic
/// (AD-3): no clock, no randomness, no I/O, no logging.
/// <para>
/// <b>Absence fails closed, by construction.</b> The prerequisite parameter is nullable and <c>null</c> yields
/// <c>ad16-screen-not-calculated</c>: it is impossible to obtain a qualifying verdict without supplying a
/// calculated screen result. If <c>Radar:Efficacy:AttentionArrival:Enabled</c> is off, the claim path is
/// therefore closed — the correct reading of a prerequisite that was never run.
/// </para>
/// <para>
/// <b>The one judgement call, stated because it borders on a precommitment (spec 170 §1.2):</b> AD-15 requires
/// the screen to be CALCULATED, not to have passed. <see cref="Ad16ScreenOutcome.Miss"/> and
/// <see cref="Ad16ScreenOutcome.ClearsNecessaryScreen"/> both satisfy the prerequisite; only
/// pending/unavailable/absent/invalid do not. Tightening this to "must not be a Miss" would be an unrecorded
/// change to a precommitted decision — out of scope here. The renderer compensates: a claim licensed beside a
/// Miss must state the Miss in the same block, before the licence sentence.
/// </para>
/// </summary>
public static class Ad15ClaimGate
{
    /// <summary>
    /// Judges the composite gate. <paramref name="priceGateReasons"/> are the harness's price-side reasons in
    /// their deterministic order; <paramref name="satisfiesPriceGate"/> is the harness's price verdict. The
    /// two are cross-checked (fail closed): an inconsistent pair can never qualify.
    /// </summary>
    public static Ad15ClaimVerdict Evaluate(
        bool satisfiesPriceGate,
        IReadOnlyList<Ad15GateReason> priceGateReasons,
        Ad15AttentionPrerequisite? attentionPrerequisite)
    {
        ArgumentNullException.ThrowIfNull(priceGateReasons);

        var prerequisite = attentionPrerequisite ?? Ad15AttentionPrerequisite.NotCalculated;

        var reasons = new List<Ad15GateReason>(priceGateReasons);
        var prerequisiteReason = PrerequisiteReason(prerequisite);
        if (prerequisiteReason is not null)
        {
            reasons.Add(prerequisiteReason);
        }

        // Fail closed on every axis: the price flag, the price reason list AND the prerequisite must all
        // agree before the composite qualifies. A caller handing a true flag beside a non-empty reason list
        // is inconsistent, and an inconsistent input must not read as a claim.
        var qualifies = satisfiesPriceGate && priceGateReasons.Count == 0 && prerequisiteReason is null;

        return new Ad15ClaimVerdict(
            Qualifies: qualifies,
            SatisfiesPriceGate: satisfiesPriceGate,
            Prerequisite: prerequisite,
            Reasons: reasons);
    }

    /// <summary>
    /// The prerequisite's gate reason, or null when it is satisfied. TOTAL over
    /// <see cref="Ad16ScreenOutcome"/> — the default arm fails closed as invalid, so an outcome this gate has
    /// never heard of can never satisfy it.
    /// </summary>
    private static Ad15GateReason? PrerequisiteReason(Ad15AttentionPrerequisite prerequisite) =>
        prerequisite.Outcome switch
        {
            Ad16ScreenOutcome.Miss => null,
            Ad16ScreenOutcome.ClearsNecessaryScreen => null,
            Ad16ScreenOutcome.NotCalculated => new Ad15GateReason(
                Ad15GateReasonCodes.Ad16ScreenNotCalculated,
                detail: "AD-16's precommitted attention-arrival screen was not run for this comparison"),
            Ad16ScreenOutcome.Unavailable => new Ad15GateReason(
                Ad15GateReasonCodes.Ad16ScreenUnavailable,
                detail: "AD-16's attention-arrival screen could not be evaluated — a configuration failure, "
                    + "not accrual"),
            Ad16ScreenOutcome.Pending => new Ad15GateReason(
                Ad15GateReasonCodes.Ad16ScreenPending,
                detail: "AD-16's attention-arrival screen has not accrued its minimum eligible dates"),
            _ => new Ad15GateReason(
                Ad15GateReasonCodes.Ad16ScreenInvalid,
                detail: "AD-16's attention-arrival screen result could not be interpreted"),
        };

    /// <summary>
    /// The stable kebab-case token for an outcome (the CSV's <c>ad16ScreenOutcome</c> column). Total: an
    /// unrecognised value renders as the invalid token, never as an invented state.
    /// </summary>
    public static string OutcomeToken(Ad16ScreenOutcome outcome) => outcome switch
    {
        Ad16ScreenOutcome.NotCalculated => "not-calculated",
        Ad16ScreenOutcome.Unavailable => "unavailable",
        Ad16ScreenOutcome.Pending => "pending",
        Ad16ScreenOutcome.Miss => "miss",
        Ad16ScreenOutcome.ClearsNecessaryScreen => "clears-necessary-screen",
        _ => "invalid",
    };
}
