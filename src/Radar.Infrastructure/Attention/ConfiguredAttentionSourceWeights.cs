using System.Globalization;
using System.Text;
using Radar.Application.Scoring;

namespace Radar.Infrastructure.Attention;

/// <summary>
/// Config-driven <see cref="IAttentionSourceWeights"/> over <see cref="AttentionSourceTierOptions"/>: builds
/// an immutable, normalised publisher → tier-weight lookup once at construction so the scoring formula stays a
/// pure, deterministic function (AD-3). Publisher names are normalised to a domain-form-tolerant key (lowercase,
/// a single trailing common-TLD token stripped, then all non-alphanumerics removed) so observed variants such as
/// <c>"marketscreener.com"</c> and <c>"Simply Wall St"</c> resolve to the same curated entry. The same
/// normalization is applied to the configured keys at load and to the incoming <c>SourceName</c> at lookup. A
/// publisher not in any tier resolves to <see cref="AttentionSourceTierOptions.UnknownWeight"/>; a blank/null
/// name likewise returns the unknown default. Fails fast (throws in the constructor) on a configured weight
/// outside [0,1] so a misconfiguration cannot silently distort scoring.
/// </summary>
public sealed class ConfiguredAttentionSourceWeights : IAttentionSourceWeights
{
    private readonly double _unknownWeight;
    private readonly IReadOnlyDictionary<string, double> _weightByPublisher;

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

        // Iterate tiers in a stable (ordinal by tier name) order so a publisher listed in two tiers resolves
        // deterministically (last-wins). All weights are validated into [0,1] before they can reach scoring.
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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

                map[key] = tier.Weight;
            }
        }

        _weightByPublisher = map;
    }

    /// <inheritdoc />
    public double WeightFor(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return _unknownWeight;
        }

        return _weightByPublisher.TryGetValue(Normalize(sourceName), out var weight)
            ? weight
            : _unknownWeight;
    }

    /// <inheritdoc />
    public string CanonicalDescriptor()
    {
        // Deterministic serialization for the scoring-config fingerprint (AD-3): the unknown default first,
        // then each publisher entry ordered by its already-normalised key (Ordinal), with culture-invariant
        // round-trip weight formatting. Stable regardless of dictionary insertion order. Publisher keys are
        // escaped so the reserved delimiters (=, ;, and the % escape char itself) cannot appear literally —
        // otherwise a name containing one could collide with a different tier map and yield the same
        // descriptor (a non-injective fingerprint input). Normal names (spaces etc.) are left unchanged.
        var builder = new StringBuilder();
        builder.Append("unknown=")
            .Append(_unknownWeight.ToString("R", CultureInfo.InvariantCulture))
            .Append(';');

        foreach (var key in _weightByPublisher.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder.Append(Escape(key))
                .Append('=')
                .Append(_weightByPublisher[key].ToString("R", CultureInfo.InvariantCulture))
                .Append(';');
        }

        return builder.ToString();
    }

    // Percent-escape the reserved descriptor delimiters so the key→value;key→value serialization stays
    // injective. The % (escape marker) MUST be replaced first, before the delimiters it encodes.
    private static string Escape(string key) =>
        key.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal);

    // The small, closed set of common web-domain suffixes stripped from a trailing (dot-prefixed) token so a
    // domain-form publisher name ("marketscreener.com") collapses onto its bare-outlet key ("MarketScreener").
    // Curated to the observed / plausible Google-News domain forms — arbitrary dotted tokens are NOT stripped.
    private static readonly string[] TrailingTlds =
        { ".com", ".st", ".io", ".net", ".org", ".co", ".ai", ".news" };

    // Normalize a publisher name to a domain-form / punctuation / spacing / case tolerant key: lowercase
    // (invariant), strip a single trailing common-TLD token (dot-prefixed, from the closed set above), then
    // remove ALL non-alphanumeric characters. So "Simply Wall St" → "simplywallst", "marketscreener.com" →
    // "marketscreener", "simplywall.st" → "simplywall". Conservative by design (no fuzzy/vowel stripping) so
    // two genuinely-distinct outlets are never collapsed onto one key. Pure static (AD-3).
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
