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
    /// Every shippable formula token, in version order (for fail-fast messages and tests). Genuinely
    /// read-only — the closed set of shippable structures must not be mutable through a downcast, or the
    /// config validator's "known formulas" and the factory's dispatch could disagree at runtime.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { V8, V9 });

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
