using System.Globalization;
using System.Text;

namespace Radar.Application.SignalExtraction;

/// <summary>
/// One dollar-materiality tier: an inclusive lower dollar bound and the Strength a signal takes when its
/// amount is at or above that bound. Reused for both the config-provided
/// <see cref="Radar.Domain.Signals.SignalType.InsiderBuying"/> net-value tiers (this file) and the code-const
/// <see cref="Radar.Domain.Signals.SignalType.GovernmentContract"/> award-amount tiers
/// (<see cref="KeywordSignalExtractor"/>) — one tier shape, one <c>StrengthForAmount</c> walk. Tables are
/// ordered DESCENDING by <see cref="MinInclusive"/>; the floor tier's bound is <see cref="decimal.MinValue"/>
/// so any amount maps. Init-only auto-properties so an <c>IConfiguration</c> JSON array of
/// <c>{ MinInclusive, Strength }</c> binds cleanly.
/// </summary>
public sealed record InsiderMaterialityTier
{
    public decimal MinInclusive { get; init; }
    public int Strength { get; init; }

    public InsiderMaterialityTier()
    {
    }

    public InsiderMaterialityTier(decimal minInclusive, int strength)
    {
        MinInclusive = minInclusive;
        Strength = strength;
    }
}

/// <summary>
/// The tunable MAGNITUDES of the deterministic extractor's <c>InsiderBuying</c> materiality scaling
/// (distinct from the phrase→direction rule STRUCTURE, which stays versioned code identity via
/// <see cref="KeywordSignalExtractor.RuleSetVersion"/>). Bound from <c>Radar:Insider:*</c>; injected into
/// <see cref="KeywordSignalExtractor"/>, which reads the buy/sell tier tables and the multi-insider cluster
/// boost from here instead of const fields. Defaults == the spec-93 values, so a blank/absent config is
/// byte-identical to the pre-96 behaviour. Splitting the single spec-93 table into separate
/// <see cref="BuyTiers"/> and <see cref="SellTiers"/> (both defaulting to the same table) is what makes a
/// deliberate buy-vs-sell asymmetry experiment expressible with NO code change. Immutable → the extractor
/// stays a pure function. These are for DELIBERATE, reasoned experiments (run different profiles to compare
/// weightings), NOT for curve-fitting weights to price/backtest outcomes — see the spec's Out of scope.
/// </summary>
public sealed record InsiderMaterialityWeights
{
    /// <summary>The spec-93 buy/sell materiality table, descending by threshold, with a decimal.MinValue floor.</summary>
    private static readonly IReadOnlyList<InsiderMaterialityTier> DefaultTiers =
    [
        new(5_000_000m, 8),
        new(1_000_000m, 7),
        new(250_000m, 6),
        new(50_000m, 4),
        new(decimal.MinValue, 2),
    ];

    /// <summary>Materiality tiers for a Positive (open-market purchase) InsiderBuying signal (spec 93 default).</summary>
    public IReadOnlyList<InsiderMaterialityTier> BuyTiers { get; init; } = DefaultTiers;

    /// <summary>Materiality tiers for a Negative (open-market sale) InsiderBuying signal (spec 93 default).</summary>
    public IReadOnlyList<InsiderMaterialityTier> SellTiers { get; init; } = DefaultTiers;

    /// <summary>The additive multi-insider (>= 2 insiders same direction) boost, capped at the domain max 10 by the extractor (spec 93's +1).</summary>
    public int ClusterBoost { get; init; } = 1;

    /// <summary>
    /// Fail-fast validation of tier tables that would break the materiality mapping or the domain
    /// Strength contract (1..10). Each table must be non-empty, have a <see cref="decimal.MinValue"/> floor
    /// (so every amount maps), be ordered strictly-descending by <see cref="InsiderMaterialityTier.MinInclusive"/>,
    /// carry only Strengths in the domain range <c>1..10</c>, and be non-increasing in Strength walking
    /// descending (a higher amount must never map to a strictly lower Strength). <see cref="ClusterBoost"/>
    /// must not be negative. Called from the extractor constructor AND from the DI binder so a misconfigured
    /// profile cannot silently produce an out-of-range Strength that fails <c>SignalValidation</c> at runtime.
    /// </summary>
    public void Validate()
    {
        ValidateTiers(BuyTiers, nameof(BuyTiers));
        ValidateTiers(SellTiers, nameof(SellTiers));

        if (ClusterBoost < 0)
        {
            throw new InvalidOperationException(
                $"Radar:Insider ClusterBoost must not be negative; was {ClusterBoost}. A negative boost would "
                    + "silently distort insider materiality scoring.");
        }
    }

    private static void ValidateTiers(IReadOnlyList<InsiderMaterialityTier> tiers, string field)
    {
        if (tiers is null || tiers.Count == 0)
        {
            throw new InvalidOperationException(
                $"Radar:Insider {field} must contain at least one tier; an empty table maps no amount.");
        }

        if (tiers[^1].MinInclusive != decimal.MinValue)
        {
            throw new InvalidOperationException(
                $"Radar:Insider {field} must end with a decimal.MinValue floor tier so every amount maps; the "
                    + $"lowest bound was {tiers[^1].MinInclusive.ToString(CultureInfo.InvariantCulture)}.");
        }

        for (var i = 0; i < tiers.Count; i++)
        {
            var strength = tiers[i].Strength;
            if (strength is < 1 or > 10)
            {
                throw new InvalidOperationException(
                    $"Radar:Insider {field} Strength must be in the domain range 1..10; tier {i} was {strength}. "
                        + "An out-of-range Strength would fail SignalValidation.");
            }

            if (i > 0)
            {
                if (tiers[i].MinInclusive >= tiers[i - 1].MinInclusive)
                {
                    throw new InvalidOperationException(
                        $"Radar:Insider {field} must be ordered strictly descending by MinInclusive; tier {i} "
                            + $"({tiers[i].MinInclusive.ToString(CultureInfo.InvariantCulture)}) is not below tier "
                            + $"{i - 1} ({tiers[i - 1].MinInclusive.ToString(CultureInfo.InvariantCulture)}).");
                }

                if (tiers[i].Strength > tiers[i - 1].Strength)
                {
                    throw new InvalidOperationException(
                        $"Radar:Insider {field} Strength must be non-increasing walking descending; tier {i} "
                            + $"(Strength {tiers[i].Strength}) is higher than tier {i - 1} "
                            + $"(Strength {tiers[i - 1].Strength}) — a smaller amount would out-score a larger one.");
                }
            }
        }
    }

    /// <summary>
    /// Deterministic, culture-invariant (AD-3) serialization hashed by the scoring-config fingerprint:
    /// <c>buy={min}:{strength},...;sell={min}:{strength},...;cluster={ClusterBoost};</c> in list order with a
    /// fixed field ordering. Numeric-only (no user strings to escape); invariant-culture formatting so a
    /// comma-decimal locale cannot corrupt it.
    /// </summary>
    public string CanonicalDescriptor()
    {
        var builder = new StringBuilder();
        AppendTiers(builder, "buy", BuyTiers);
        AppendTiers(builder, "sell", SellTiers);
        builder.Append("cluster=")
            .Append(ClusterBoost.ToString(CultureInfo.InvariantCulture))
            .Append(';');
        return builder.ToString();
    }

    private static void AppendTiers(StringBuilder builder, string key, IReadOnlyList<InsiderMaterialityTier> tiers)
    {
        builder.Append(key).Append('=');
        for (var i = 0; i < tiers.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(tiers[i].MinInclusive.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(tiers[i].Strength.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append(';');
    }
}
