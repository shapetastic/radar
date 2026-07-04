namespace Radar.Infrastructure.Attention;

/// <summary>
/// Curated, config-driven source-quality tiers for the scoring formula's attention-breadth reach term,
/// bound from <c>Radar:Attention</c>. Each named tier carries a per-publisher <see cref="SourceTier.Weight"/>
/// in [0,1] and the list of publisher <c>SourceName</c>s in that tier; a publisher not in any tier gets
/// <see cref="UnknownWeight"/>. This is the Infrastructure config data behind the Application
/// <c>IAttentionSourceWeights</c> abstraction (AD-5) — the "what counts as genuine market notice" policy.
/// <para>
/// Calibration (spec 90 recalibration): the default posture is <b>denylist-expand + a lower unknown default</b>
/// (<see cref="UnknownWeight"/> <c>0.25</c>) so an all-aggregator name scores materially lower Attention than a
/// genuinely-covered one — the mill denylist was expanded with the observed long-tail aggregators and the
/// unknown default was dropped from <c>0.5</c> to <c>0.25</c>. Unknown publishers stay <b>non-zero</b> (never
/// silently zeroed): a real outlet not yet on a list is <i>under</i>-counted, not dropped. The documented
/// alternative posture is an <b>allowlist flip</b> (unknown ≈ mill level, only curated genuine outlets earn
/// full weight) — tunable purely in config (edit <see cref="UnknownWeight"/> + the genuine list, no code change).
/// </para>
/// <para>
/// Maintenance / false-positive risk (documented honestly): a curated mill list is inherently arbitrary and
/// needs upkeep — new content mills appear and a legitimate niche outlet could be mis-tagged. Publisher name
/// strings vary ("Simply Wall St" vs "Simplywall.st", "marketscreener.com" vs "MarketScreener") — this is now
/// handled by the matcher's domain-form normalization (lowercase, trailing-TLD strip, punctuation/spacing
/// removal) plus explicit aliases for word-boundary variants the domain elides. Kept small and config-driven
/// (edit <c>appsettings</c>, no code change), NOT a comprehensive reputation database.
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
    /// The weight for a publisher not present in any tier (recalibrated default 0.25, spec 90). Deliberately
    /// <b>non-zero</b> so a real outlet not yet on a list is <i>under</i>-counted (a quarter of a genuine outlet)
    /// rather than being silently zeroed. Lowered from <c>0.5</c> so the Google-News long tail of unrecognised
    /// aggregators no longer counts as half a genuine outlet and wash out the tiering. The documented alternative
    /// is an allowlist flip (drop this near mill level, ≈ <c>0.15</c>, and rely on the genuine list) — tunable in
    /// config without a code change.
    /// </summary>
    public double UnknownWeight { get; init; } = 0.25;

    /// <summary>The named source-quality tiers (bindable from <c>Radar:Attention:SourceTiers</c>).</summary>
    public IReadOnlyDictionary<string, SourceTier> SourceTiers { get; init; }
        = new Dictionary<string, SourceTier>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The curated code-level fallback so a no-config run still tiers sensibly: content mills at 0.1,
    /// genuine outlets at 1.0, and unknown publishers at the recalibrated 0.25 default (spec 90). The mill
    /// denylist expands the spec-88 seed with the observed long-tail aggregators plus the explicit
    /// <c>Simplywall.st</c> alias (the domain form elides a word, so normalization alone cannot bridge it).
    /// </summary>
    public static AttentionSourceTierOptions Default { get; } = new()
    {
        UnknownWeight = 0.25,
        SourceTiers = new Dictionary<string, SourceTier>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mill"] = new SourceTier
            {
                Weight = 0.1,
                Publishers = new[]
                {
                    "MarketBeat", "Zacks", "Simply Wall St", "StockStory", "Moomoo", "TradingView",
                    "Stock Titan", "GuruFocus", "Defense World", "Pluang", "MarketScreener",
                    "Finviz", "Investing.com", "Insider Monkey", "Benzinga", "TipRanks", "StockAnalysis",
                    "Simplywall.st",
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
