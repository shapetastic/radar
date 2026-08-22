using Microsoft.Extensions.Configuration;

using System.Reflection;

namespace Radar.Infrastructure.DependencyInjection;

/// <summary>
/// The TWO config-section guards that are universal across every bound option type (spec 174), extracted from
/// the spec-149 inline-<c>Weights</c> pattern so the four older scoring-affecting binders (named scoring
/// profiles, insider tiers, media collapse, attention tiers) close the same fail-open ONCE instead of pasting
/// the pattern four times. <c>ConfigurationBinder</c> fails OPEN in two ways these guards close:
/// <list type="bullet">
/// <item>a section that exists but carries a scalar <c>Value</c> where an object was meant binds to
/// <c>null</c>, which every pre-174 call site null-coalesced onto the CODE DEFAULTS — so a mis-shaped
/// experiment ran, stamped and got ranked while measuring nothing (the arch-sweep M-1 shape);</item>
/// <item>a config key that matches no property is silently ignored, so a typo'd weight/tier key left the
/// default value in place while the run read as tuned (the exact shape specs 138 and 149 each closed once).</item>
/// </list>
/// Per-entry VALUE-shape rules are deliberately NOT here: they are not universal (<c>ScoringWeights</c> is
/// all-numeric, the insider profile carries tier LISTS, attention carries a free-keyed DICTIONARY), so each
/// call site keeps its own — this is two guards and a name-set derivation, not a generic recursive validator.
/// <para>
/// PUBLIC since spec 177: the Worker's composition root applies the same two guards to its own
/// <c>Radar:NewsResearch</c> options block, and Worker cannot see Infrastructure internals — a second copy
/// there is exactly the drift these guards were extracted to prevent (CLAUDE.md reuse-over-copy).
/// </para>
/// </summary>
public static class ConfigSectionGuards
{
    /// <summary>
    /// The public readable+writable instance property names of <paramref name="optionsType"/> — the ONE
    /// derivation behind every spec-174 unknown-key allowlist (and behind
    /// <c>InfrastructureServiceCollectionExtensions.ScoringWeightNames</c>, which routes through it so the
    /// set is never derived twice). <c>BindingFlags.Instance</c> deliberately excludes statics (e.g.
    /// <c>AttentionSourceTierOptions.Default</c> must never read as a bindable key), and get-only properties
    /// are excluded because the binder cannot set them (e.g. <c>MediaCollapseOptions.EventWindow</c>).
    /// <para>
    /// <b>Case-INSENSITIVE, deliberately.</b> <c>ConfigurationBinder</c> matches config keys to properties
    /// case-insensitively, so the validator must use the SAME comparison or its verdict on what is "unknown"
    /// stops being the question the binder answers — see the fuller rationale on
    /// <c>InfrastructureServiceCollectionExtensions.ScoringWeightNames</c>.
    /// </para>
    /// </summary>
    public static HashSet<string> BindablePropertyNames(Type optionsType) =>
        optionsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scalar-section shape guard (mirrors the spec-149 guard in <c>ApplyInlineWeightOverrides</c>): a
    /// section that carries a scalar <c>Value</c> and no children (e.g. <c>"Profiles": { "x": "0.1" }</c>)
    /// can never bind to an object and would otherwise silently fall through to the code defaults. A section
    /// whose value is null/whitespace with no children (an explicitly-null section, or <c>--Radar:X=</c> on
    /// the command line) is NOT a scalar — callers treat it as their honest "all defaults" case.
    /// <paramref name="expectedShape"/> states the expected shape and the remedy; the thrown message leads
    /// with the exact section path and the offending scalar.
    /// </summary>
    public static void FailIfScalarSection(IConfigurationSection section, string expectedShape)
    {
        if (!string.IsNullOrWhiteSpace(section.Value) && !section.GetChildren().Any())
        {
            throw new InvalidOperationException(
                $"{section.Path} is the scalar '{section.Value}'; {expectedShape}");
        }
    }

    /// <summary>
    /// The unknown-key allowlist guard: each immediate child key of <paramref name="section"/> must be one of
    /// <paramref name="validNames"/> (derive via <see cref="BindablePropertyNames"/>, or reuse an existing
    /// set such as <c>ScoringWeightNames</c> — never derive one type's set twice). The message follows the
    /// spec-149 shape: the offending child path, the key, the per-site <paramref name="consequence"/>
    /// (which should end mid-sentence, e.g. "…must name a scoring weight"), then the sorted valid names.
    /// </summary>
    public static void FailOnUnknownKeys(
        IConfigurationSection section,
        IReadOnlySet<string> validNames,
        string targetTypeName,
        string consequence)
    {
        foreach (var child in section.GetChildren())
        {
            if (!validNames.Contains(child.Key))
            {
                throw new InvalidOperationException(
                    $"{child.Path} names '{child.Key}', which is not a {targetTypeName} field, {consequence} "
                        + $"(valid names: {string.Join(", ", validNames.Order(StringComparer.Ordinal))}).");
            }
        }
    }
}
