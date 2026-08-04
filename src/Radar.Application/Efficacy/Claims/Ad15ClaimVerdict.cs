namespace Radar.Application.Efficacy.Claims;

/// <summary>
/// The COMPOSITE AD-15 verdict (spec 170): the price half AND the AD-16 attention prerequisite, judged
/// together by <see cref="Ad15ClaimGate"/>. Only <see cref="Qualifies"/> licenses the "adding value" sentence;
/// <see cref="SatisfiesPriceGate"/> alone never does — a price-side result must be unable to read as the
/// claim.
/// </summary>
/// <param name="Qualifies">
/// True exactly when <see cref="Reasons"/> is empty: the price gate passed AND AD-16's screen was actually
/// calculated (a <c>Miss</c> satisfies the prerequisite — AD-15 requires the screen to be CALCULATED, not
/// passed — but the renderer must state the Miss beside the licence sentence).
/// </param>
/// <param name="SatisfiesPriceGate">The price half alone (the harness's <c>SatisfiesPriceGate</c>).</param>
/// <param name="Prerequisite">
/// The prerequisite that was judged — never null: an absent prerequisite is represented as
/// <see cref="Ad15AttentionPrerequisite.NotCalculated"/>, so the verdict always says WHAT the attention side
/// looked like, including "nobody ran it".
/// </param>
/// <param name="Reasons">
/// Every reason the composite gate did not pass, structured and closed-coded: the price reasons first (in the
/// harness's deterministic order, rendered text unchanged), then the prerequisite reason when unmet. Empty
/// exactly when <see cref="Qualifies"/> is true.
/// </param>
public sealed record Ad15ClaimVerdict(
    bool Qualifies,
    bool SatisfiesPriceGate,
    Ad15AttentionPrerequisite Prerequisite,
    IReadOnlyList<Ad15GateReason> Reasons);
