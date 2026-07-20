namespace Radar.Domain.Companies;

/// <summary>
/// Curated "how followed already" tier of a watch-universe company — seed metadata describing how
/// noticed/covered the name already is (a mega-cap is maximally followed; an under-covered small-cap is
/// not). It is hand-curated in the company seed and is <b>NEVER</b> derived from price, market cap, or
/// volume (AD-14: price data must not enter scoring); it exists so the Opportunity attention-discount can
/// distinguish true notedness that Radar's own-feed publisher breadth cannot see.
/// <see cref="Small"/> (= 0) is the fail-safe default: an absent/unrecognized tier means no extra discount.
/// </summary>
public enum FollowingTier
{
    /// <summary>Under-followed small-cap (the default): no extra following discount.</summary>
    Small = 0,

    /// <summary>Moderately followed mid-cap.</summary>
    Mid,

    /// <summary>Well-followed large-cap.</summary>
    Large,

    /// <summary>Maximally followed mega-cap benchmark/control (e.g. the seed's AAPL/JNJ/CAT).</summary>
    Mega,
}
