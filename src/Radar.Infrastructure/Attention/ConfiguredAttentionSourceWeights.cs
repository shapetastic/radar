using Radar.Application.Scoring;

namespace Radar.Infrastructure.Attention;

/// <summary>
/// Config-driven <see cref="IAttentionSourceWeights"/> over <see cref="AttentionSourceTierOptions"/>: builds
/// an immutable, whitespace-normalised, case-insensitive publisher → tier-weight lookup once at construction
/// so the scoring formula stays a pure, deterministic function (AD-3). A publisher not in any tier resolves to
/// <see cref="AttentionSourceTierOptions.UnknownWeight"/>; a blank/null name likewise returns the unknown
/// default. Fails fast (throws in the constructor) on a configured weight outside [0,1] so a misconfiguration
/// cannot silently distort scoring.
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

    // Trim and collapse internal whitespace runs so "Simply  Wall St" and "Simply Wall St" resolve equally;
    // the lookup itself is OrdinalIgnoreCase so case need not be normalised here.
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
