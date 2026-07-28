namespace Radar.Application.Scoring;

/// <summary>
/// The ONE definition of the shipped <see cref="IScoreFormula.Version"/> tokens and of which of them a
/// strategy may name (spec 146). The formula CLASS is a code decision (AD-6) — a formula
/// structure/shape change is a new <c>radar-formula-vN</c> class, never a config edit — but WHICH shipped
/// class a strategy runs is now a per-strategy choice (<see cref="ScoringStrategyDefinition.Formula"/>), so
/// the name list has to exist somewhere both the config validator and the formula factory can read it.
/// Putting it here means the validator's "known formulas" message and the factory's dispatch can never
/// disagree about what is shippable.
/// <para>
/// The tokens are the exact strings each formula returns from <see cref="IScoreFormula.Version"/> and that
/// <c>ScoringConfigFingerprint</c> hashes as its <c>formula</c> field, so they are persisted identities:
/// renaming one is a deliberate, visible re-stamp of every strategy that ran it, exactly like renaming
/// anything else Radar persists by name.
/// </para>
/// </summary>
public static class ScoreFormulaVersions
{
    /// <summary>The shipped default (spec 122). Every strategy that does not name a formula runs this.</summary>
    public const string V8 = "radar-formula-v8";

    /// <summary>The channel-composition formula (spec 146). Opt-in per strategy; v8 is untouched.</summary>
    public const string V9 = "radar-formula-v9";

    /// <summary>
    /// The channel-composition formula in which a collector channel with NO NET DIRECTIONAL MASS contributes
    /// exactly <c>0</c> rather than half its saturated share (spec 153). Opt-in per strategy; v8 and v9 are
    /// both untouched and remain available as the controls that make the change measurable.
    /// </summary>
    public const string V10 = "radar-formula-v10";

    /// <summary>
    /// The channel-composition formula whose collector-channel SATURATION is computed over
    /// <b>directional-only</b> activity (spec 157, binding under AD-16's "neutral volume must never amplify a
    /// directional read"): a Neutral signal changes a collector channel's score by exactly 0, where under
    /// <see cref="V10"/> it still raised the channel's saturation and thereby amplified a directional read.
    /// v11 additionally REJECTS a breadth channel at startup (spec 158 measured positive-only breadth as
    /// structurally zero, and unfiltered breadth would let a Neutral news item raise the score — see
    /// <c>docs/158-channel-feasibility-findings.md</c>). Opt-in per strategy; v8, v9 and v10 are all untouched
    /// and remain available as the controls that make the change measurable.
    /// </summary>
    public const string V11 = "radar-formula-v11";

    /// <summary>
    /// The <b>CONTROL</b> (spec 154): a channel formula whose collector channels score the plain saturated
    /// <b>COUNT</b> of the signals they consumed — no direction, no notedness, no quality weighting. It exists
    /// to be BEATEN, not to be run as a candidate strategy.
    /// <para>
    /// <b>It is deliberately NOT <c>radar-formula-v11</c>.</b> The <c>radar-formula-vN</c> sequence is the
    /// lineage of Radar's COMPOSITE — each version a considered evolution of the previous one (AD-6) — and this
    /// is not an evolution of anything: it is the embarrassingly simple heuristic the composite has to
    /// out-perform before it can be described as adding value (AD-15). Numbering it in that sequence would say
    /// the opposite of what it is, and spec 154's §3 requires that a baseline's NAME says what it is wherever
    /// it appears — in a leaderboard, in a fingerprint record, in a snapshot's <c>ComponentJson</c>.
    /// </para>
    /// </summary>
    public const string BaselineActivityV1 = "radar-baseline-activity-v1";

    /// <summary>
    /// Every shippable formula token: the <c>radar-formula-vN</c> composite lineage in version order, then the
    /// baseline CONTROLS (spec 154), which are not part of that lineage. Genuinely read-only — the closed set
    /// of shippable structures must not be mutable through a downcast, or the config validator's "known
    /// formulas" and the factory's dispatch could disagree at runtime.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(new[] { V8, V9, V10, V11, BaselineActivityV1 });

    /// <summary>
    /// The formulas that COMPOSE their score from a <see cref="ScoringChannelSet"/> — currently
    /// <see cref="V9"/>, <see cref="V10"/>, <see cref="V11"/> and <see cref="BaselineActivityV1"/>.
    /// <para>
    /// ONE predicate, deliberately, because three separate places have to agree about it: the "this formula
    /// needs channels" rule, the "this formula must not declare channels" rule (both in
    /// <see cref="ScoringStrategySet"/>) and <see cref="RadarScoreFormulaFactory"/>'s dispatch. Spec 146
    /// hard-coded <see cref="V9"/> in the first two; adding v10 against that shape would have let a v10
    /// strategy declare channels that the validator silently permitted and a rule elsewhere rejected. If they
    /// can drift they eventually will.
    /// </para>
    /// </summary>
    public static bool ConsumesChannels(string? name) =>
        Canonicalize(name) is { } canonical
        && (string.Equals(canonical, V9, StringComparison.Ordinal)
            || string.Equals(canonical, V10, StringComparison.Ordinal)
            || string.Equals(canonical, V11, StringComparison.Ordinal)
            || string.Equals(canonical, BaselineActivityV1, StringComparison.Ordinal));

    /// <summary>
    /// True when <paramref name="name"/> canonicalises onto <see cref="V11"/>, the composite formula whose
    /// configuration contract REJECTS a breadth channel (spec 157 §3, amended after spec 158's measurement).
    /// ONE predicate, for the same reason <see cref="ConsumesChannels"/> is one: the config-boundary rule in
    /// <see cref="ScoringStrategySet"/> and the constructor guard in <c>RadarScoreFormulaV11</c> must agree
    /// about which formula refuses breadth, or a budget the validator permitted would explode later.
    /// (<see cref="BaselineActivityV1"/> ALSO refuses breadth, in its own constructor and for its own reason —
    /// tier-weighted reach is a quality weighting a "no quality weighting" control must not contain; that
    /// refusal predates this predicate and deliberately stays where it is.)
    /// </summary>
    public static bool RejectsBreadthChannels(string? name) =>
        Canonicalize(name) is { } canonical && string.Equals(canonical, V11, StringComparison.Ordinal);

    /// <summary>
    /// The comma-separated channel-composition tokens, for fail-fast messages. Rendered FROM
    /// <see cref="All"/> through <see cref="ConsumesChannels"/>, so a message can never name a different set
    /// from the one the rules enforce.
    /// </summary>
    public static string ChannelFormulaList => string.Join(", ", All.Where(ConsumesChannels));

    /// <summary>
    /// Canonicalises a configured formula name onto one of <see cref="All"/>: trims, matches
    /// case-insensitively, and returns the canonical constant. Returns <c>null</c> for a blank or unknown
    /// name so the caller can fail fast with a message that names the strategy — a silent fallthrough to the
    /// default would let a typo'd formula quietly score a strategy with the wrong structure.
    /// </summary>
    public static string? Canonicalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="name"/> canonicalises onto a shipped formula token.</summary>
    public static bool IsKnown(string? name) => Canonicalize(name) is not null;

    /// <summary>The comma-separated shippable tokens, for fail-fast messages.</summary>
    public static string KnownList => string.Join(", ", All);
}
