using System.Globalization;
using System.Text;
using Radar.Application.Scoring;

namespace Radar.Infrastructure.Attention;

/// <summary>
/// Config-driven <see cref="IAttentionSourceWeights"/> over <see cref="AttentionSourceTierOptions"/>: builds
/// an immutable, normalised publisher → (tier name, tier weight) lookup once at construction so the scoring
/// formula stays a pure, deterministic function (AD-3). Publisher names are normalised to a
/// domain-form-tolerant key (lowercase, a single trailing common-TLD token stripped, then all
/// non-alphanumerics removed) so observed variants of the same outlet such as <c>"marketscreener.com"</c> and
/// <c>"MarketScreener"</c> resolve to the same curated entry. The same normalization is applied to the
/// configured keys at load and to the incoming <c>SourceName</c> at lookup. A publisher not in any tier
/// resolves to <see cref="AttentionSourceTierOptions.UnknownWeight"/>; a blank/null name likewise returns the
/// unknown default. Fails fast (throws in the constructor) on a configured weight outside [0,1] so a
/// misconfiguration cannot silently distort scoring.
/// <para>
/// <b>Spec 196 §3: <see cref="Resolve"/> is the ONE matching implementation and <see cref="WeightFor"/> is a
/// thin projection of it.</b> The stored value is the (tier name, weight) PAIR rather than a bare weight,
/// because the inverted unknown default (0.1 — the <c>Mill</c> weight) makes an explicitly-classified mill
/// and an unclassified publisher numerically identical. The capture-flow coverage diagnostic needs that
/// distinction, and a second copy of the matching rules would drift from the one the score uses.
/// </para>
/// <para>
/// <b>An ambiguous publisher FAILS FAST.</b> A normalized key claimed by two different tiers used to resolve
/// by ordinal last-wins, which made both the score and the diagnostic depend on tier-NAME ordering — an
/// invisible dependency a rename could flip. A publisher belongs to exactly one tier; "cannot tell" must
/// never resolve to "whichever sorted last".
/// </para>
/// </summary>
public sealed class ConfiguredAttentionSourceWeights : IAttentionSourceWeights
{
    /// <summary>One curated map entry: the tier that claims the key, its weight, and the listed name.</summary>
    private readonly record struct TierEntry(string TierName, double Weight, string ListedPublisher);

    private readonly double _unknownWeight;
    private readonly IReadOnlyDictionary<string, TierEntry> _entryByPublisher;

    public ConfiguredAttentionSourceWeights(AttentionSourceTierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.UnknownWeight is < 0 or > 1)
        {
            throw new InvalidOperationException(
                $"Radar:Attention UnknownWeight must be in [0,1]; was {options.UnknownWeight}. A weight "
                    + "outside [0,1] would silently distort attention scoring.");
        }

        _unknownWeight = options.UnknownWeight;

        // Iterate tiers in a stable (ordinal by tier name) order so the pair named in an ambiguity failure
        // is deterministic (AD-3). All weights are validated into [0,1] before they can reach scoring.
        var map = new Dictionary<string, TierEntry>(StringComparer.OrdinalIgnoreCase);
        var tiers = options.SourceTiers ?? new Dictionary<string, AttentionSourceTierOptions.SourceTier>();
        foreach (var tierName in tiers.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var tier = tiers[tierName];
            if (tier is null)
            {
                continue;
            }

            if (tier.Weight is < 0 or > 1)
            {
                throw new InvalidOperationException(
                    $"Radar:Attention tier '{tierName}' Weight must be in [0,1]; was {tier.Weight}. A weight "
                        + "outside [0,1] would silently distort attention scoring.");
            }

            foreach (var publisher in tier.Publishers ?? Array.Empty<string>())
            {
                var key = Normalize(publisher);
                if (key.Length == 0)
                {
                    continue;
                }

                if (map.TryGetValue(key, out var existing))
                {
                    // Same tier twice (a duplicate entry, or two spellings that normalize alike) is
                    // idempotent — it names one tier, so nothing is ambiguous.
                    if (string.Equals(existing.TierName, tierName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Radar:Attention publisher '{publisher}' (normalized key '{key}', also listed as "
                            + $"'{existing.ListedPublisher}') is claimed by two tiers: '{existing.TierName}' "
                            + $"and '{tierName}'. A publisher belongs to exactly one tier — resolving by "
                            + "ordinal last-wins would make both the attention score and the publisher "
                            + "coverage diagnostic depend on tier-NAME ordering, which a rename could flip.");
                }

                map[key] = new TierEntry(tierName, tier.Weight, publisher);
            }
        }

        _entryByPublisher = map;
    }

    /// <inheritdoc />
    public AttentionSourceResolution Resolve(string? sourceName)
    {
        var key = Normalize(sourceName);

        return _entryByPublisher.TryGetValue(key, out var entry)
            ? AttentionSourceResolution.Mapped(entry.TierName, entry.Weight, key)
            : AttentionSourceResolution.Unclassified(_unknownWeight, key);
    }

    /// <summary>
    /// <inheritdoc cref="IAttentionSourceWeights.WeightFor" path="/summary/node()"/>
    /// </summary>
    /// <remarks>
    /// Declared on the class as well as defaulted on the interface so concretely-typed callers keep
    /// compiling; it is the same one-line projection either way.
    /// </remarks>
    public double WeightFor(string? sourceName) => Resolve(sourceName).Weight;

    /// <inheritdoc />
    public string CanonicalDescriptor()
    {
        // Deterministic serialization for the scoring-config fingerprint (AD-3): the unknown default first,
        // then each publisher entry ordered by its already-normalised key (Ordinal), with culture-invariant
        // round-trip weight formatting. Stable regardless of dictionary insertion order. Publisher keys are
        // escaped so the reserved delimiters (=, ;, and the % escape char itself) cannot appear literally —
        // otherwise a name containing one could collide with a different tier map and yield the same
        // descriptor (a non-injective fingerprint input). Normal names (spaces etc.) are left unchanged.
        //
        // TIER NAMES ARE DELIBERATELY ABSENT (spec 196). The descriptor's SHAPE is unchanged by the spec-196
        // resolver: two tier maps with the same membership and the same weights score identically, so a tier
        // RENAME must not re-stamp a series. The pins move because the MAP moved — that must be the only
        // reason they moved.
        var builder = new StringBuilder();
        builder.Append("unknown=")
            .Append(_unknownWeight.ToString("R", CultureInfo.InvariantCulture))
            .Append(';');

        foreach (var key in _entryByPublisher.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder.Append(DescriptorEscaping.Escape(key))
                .Append('=')
                .Append(_entryByPublisher[key].Weight.ToString("R", CultureInfo.InvariantCulture))
                .Append(';');
        }

        return builder.ToString();
    }

    // Reserved-delimiter escaping is the shared DescriptorEscaping.Escape (CLAUDE.md reuse rule): one primitive
    // for every fingerprint descriptor, so the escaping cannot drift between call sites. Byte-identical here —
    // Normalize() has already stripped every non-alphanumeric character from these keys, so no delimiter can
    // survive to be escaped; the call is retained as a defence-in-depth injectivity guarantee (AD-3).

    // The small, closed set of common web-domain suffixes stripped from a trailing (dot-prefixed) token so a
    // domain-form publisher name ("marketscreener.com") collapses onto its bare-outlet key ("MarketScreener").
    // Curated to the observed / plausible Google-News domain forms — arbitrary dotted tokens are NOT stripped.
    private static readonly string[] TrailingTlds =
        { ".com", ".st", ".io", ".net", ".org", ".co", ".ai", ".news" };

    // Normalize a publisher name to a domain-form / punctuation / spacing / case tolerant key: lowercase
    // (invariant), strip a single trailing common-TLD token (dot-prefixed, from the closed set above), then
    // remove ALL non-alphanumeric characters. So "Simply Wall St" → "simplywallst", "marketscreener.com" →
    // "marketscreener", "simplywall.st" → "simplywall". Conservative by design (no fuzzy/vowel stripping); it
    // still removes punctuation/spacing, so distinct names differing only by those characters can collapse onto
    // one key — it minimises, not eliminates, cross-outlet collisions. Pure static (AD-3).
    //
    // SPEC 196 DELIBERATELY DID NOT BROADEN THIS. The regional-edition variants it needed
    // ("Investing.com Nigeria" → investingcomnigeria) are handled by explicit alias entries in the tier
    // lists, because a prefix rule here could silently collapse genuinely unrelated outlets.
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.Trim().ToLowerInvariant();

        // Strip at most one trailing common-TLD token. Membership is checked against the full closed set so
        // ".co" cannot falsely truncate ".com" (a ".com" string does not end with ".co"). The leading dot is
        // required so a name like "SpaceNews" (no dot) is not stripped by ".news".
        foreach (var tld in TrailingTlds)
        {
            if (lowered.Length > tld.Length && lowered.EndsWith(tld, StringComparison.Ordinal))
            {
                lowered = lowered[..^tld.Length];
                break;
            }
        }

        var builder = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
