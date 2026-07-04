namespace Radar.Infrastructure.Attention;

/// <summary>
/// Curated, config-driven source-quality tiers for the scoring formula's attention-breadth reach term,
/// bound from <c>Radar:Attention</c>. Each named tier carries a per-publisher <see cref="SourceTier.Weight"/>
/// in [0,1] and the list of publisher <c>SourceName</c>s in that tier; a publisher not in any tier gets
/// <see cref="UnknownWeight"/>. This is the Infrastructure config data behind the Application
/// <c>IAttentionSourceWeights</c> abstraction (AD-5) — the "what counts as genuine market notice" policy.
/// <para>
/// Maintenance / false-positive risk (documented honestly): a curated mill list is inherently arbitrary and
/// needs upkeep — new content mills appear, publisher name strings vary ("Simply Wall St" vs "Simplywall.st"),
/// and a legitimate niche outlet could be mis-tagged. This is mitigated by keeping the lists small and
/// config-driven (edit <c>appsettings</c>, no code change), defaulting unknown publishers to a conservative
/// non-zero <see cref="UnknownWeight"/> (worst case: an un-listed real outlet is <i>under</i>-counted, never
/// silently zeroed), and NOT attempting a comprehensive reputation database.
/// </para>
/// </summary>
public sealed class AttentionSourceTierOptions
{
    /// <summary>
    /// A single named source-quality tier: a per-publisher breadth weight in [0,1] and the publishers it
    /// applies to (matched case-insensitively, whitespace-normalised).
    /// </summary>
    public sealed class SourceTier
    {
        /// <summary>The attention-breadth weight applied to each publisher in this tier (in [0,1]).</summary>
        public double Weight { get; init; }

        /// <summary>The publisher <c>SourceName</c>s that fall into this tier.</summary>
        public IReadOnlyList<string> Publishers { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The weight for a publisher not present in any tier (default 0.5). Deliberately non-zero and
    /// conservative so a real outlet not yet on the allowlist counts at half a genuine outlet rather than
    /// being silently zeroed.
    /// </summary>
    public double UnknownWeight { get; init; } = 0.5;

    /// <summary>The named source-quality tiers (bindable from <c>Radar:Attention:SourceTiers</c>).</summary>
    public IReadOnlyDictionary<string, SourceTier> SourceTiers { get; init; }
        = new Dictionary<string, SourceTier>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The curated code-level fallback so a no-config run still tiers sensibly: content mills at 0.1,
    /// genuine outlets at 1.0, and unknown publishers at the 0.5 default. Matches the spec-88 seed lists.
    /// </summary>
    public static AttentionSourceTierOptions Default { get; } = new()
    {
        UnknownWeight = 0.5,
        SourceTiers = new Dictionary<string, SourceTier>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mill"] = new SourceTier
            {
                Weight = 0.1,
                Publishers = new[]
                {
                    "MarketBeat", "Zacks", "Simply Wall St", "StockStory", "Moomoo", "TradingView",
                    "Stock Titan", "GuruFocus", "Defense World", "Pluang", "MarketScreener",
                },
            },
            ["Genuine"] = new SourceTier
            {
                Weight = 1.0,
                Publishers = new[]
                {
                    "Reuters", "Bloomberg", "The Wall Street Journal", "CNBC", "Associated Press",
                    "Financial Times", "SpaceNews",
                },
            },
        },
    };
}
