namespace Radar.Infrastructure.Attention;

/// <summary>
/// Curated, config-driven source-quality tiers for the scoring formula's attention-breadth reach term,
/// bound from <c>Radar:Attention</c>. Each named tier carries a per-publisher <see cref="SourceTier.Weight"/>
/// in [0,1] and the list of publisher <c>SourceName</c>s in that tier; a publisher not in any tier gets
/// <see cref="UnknownWeight"/>. This is the Infrastructure config data behind the Application
/// <c>IAttentionSourceWeights</c> abstraction (AD-5) — the "what counts as genuine market notice" policy.
///
/// <para>
/// <b>THE TIER POLICY (spec 196 §2), defined BEFORE any publisher was assigned.</b> The definitions are the
/// thing that gets reviewed; the audit at <c>docs/cohorts/attention-publisher-audit-v1.md</c> determines
/// publisher <b>MEMBERSHIP ONLY</b> and never a tier's weight, which is a spec decision and lives here.
/// </para>
/// <list type="bullet">
/// <item><b><c>Wire</c> (0.05)</b> — paid or company-originated distribution. Confers visibility,
/// <b>not independent notice</b>, because the company controls whether the item exists at all.</item>
/// <item><b><c>Mill</c> (0.1)</b> — automated, templated or republished material with no demonstrated
/// independent selection. The test is <i>selection</i>: does this outlet decide which companies to cover,
/// or does it publish on every ticker by construction?</item>
/// <item><b><c>Platform</c> (0.3)</b> — investor-content platforms carrying a mixture of contributor
/// analysis and syndication: a human chose to write about <i>this</i> company, but the outlet exercises
/// little editorial gatekeeping. Three times <c>Mill</c> because a human chose the company; materially
/// below a professionally gated newsroom because almost nothing was gatekept. <b>Seeking Alpha and The
/// Motley Fool are both here</b> — they are the same class of outlet, and splitting them tenfold was the
/// unprincipled curation spec 196 exists to remove.</item>
/// <item><b><c>Genuine</c> (1.0)</b> — independent reporting or editorial selection.</item>
/// </list>
///
/// <para>
/// <b>Calibration (spec 196 §1 — the inversion).</b> Spec 90's posture was denylist-expand with a
/// quarter-strength unknown default; measured over the live corpus that left <b>50.1 % of observations
/// unclassified at 0.25 — two and a half times a Mill publisher</b> while genuine notice was 0.5 %, so
/// attention was measuring aggregator database coverage rather than market notice. Genuine outlets are a
/// short enumerable list and content mills are an unbounded long tail, so enumerating the tail is
/// unwinnable. The default is now an <b>allowlist</b>: an explicit entry is required to count as
/// <i>notice</i> rather than to be <i>discounted</i>.
/// </para>
/// <para>
/// Maintenance / false-positive risk (documented honestly): a curated map is inherently arbitrary and needs
/// upkeep — new mills appear and a legitimate niche outlet could be mis-tagged (which, since the inversion,
/// costs it 0.1 rather than 0.25). Publisher name strings vary ("Simply Wall St" vs "Simplywall.st",
/// "marketscreener.com" vs "MarketScreener") — handled by the matcher's domain-form normalization
/// (lowercase, trailing-TLD strip, punctuation/spacing removal) plus explicit ALIASES for the variants
/// normalization cannot bridge (regional editions such as "Investing.com Nigeria", which normalizes to
/// <c>investingcomnigeria</c>). Aliases are preferred over broadening <c>Normalize</c>, so a matching change
/// can never silently collapse unrelated outlets. Kept config-driven (edit <c>appsettings</c>, no code
/// change), NOT a comprehensive reputation database.
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
    /// The weight for a publisher not present in any tier. <b>Spec 196 §1 inverted this from 0.25 to 0.1 —
    /// the <c>Mill</c> weight.</b> An explicit entry is now required to count as <i>notice</i>, not to be
    /// <i>discounted</i>: an unrecognised publisher is treated as low-signal coverage rather than as
    /// quarter-strength genuine notice.
    /// <para>
    /// It stays deliberately <b>NON-ZERO</b>. The original reason holds — real coverage is never silently
    /// zeroed — and a zero would make an unclassified genuine outlet invisible rather than merely quiet;
    /// 0.1 keeps it present and discountable. It also stays <b>configurable</b>, so the inversion is a
    /// declared default rather than a hard-coded belief.
    /// </para>
    /// <para>
    /// Consequence, and the reason <c>IAttentionSourceWeights.Resolve</c> exists (spec 196 §3): an
    /// explicitly-classified <c>Mill</c> publisher and an unclassified one now return the same NUMBER, so a
    /// weight can no longer answer "have we ever looked at this outlet?". Only the resolver can.
    /// </para>
    /// </summary>
    public double UnknownWeight { get; init; } = 0.1;

    /// <summary>The named source-quality tiers (bindable from <c>Radar:Attention:SourceTiers</c>).</summary>
    public IReadOnlyDictionary<string, SourceTier> SourceTiers { get; init; }
        = new Dictionary<string, SourceTier>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The curated code-level fallback so a no-config run still tiers sensibly: the four spec-196 tiers, and
    /// unclassified publishers at the inverted 0.1 default.
    /// <para>
    /// <b>Membership authority:</b> every publisher added by spec 196 is classified from what it actually
    /// published in the live corpus, recorded item by item in
    /// <c>docs/cohorts/attention-publisher-audit-v1.md</c> (sampling rule: the most-recent in-corpus item
    /// per company by <c>PublishedAtUtc</c> then <c>ObservationId</c>, up to ten companies). Nothing here is
    /// classified on reputation — MarketWatch is <c>Mill</c> precisely because all ten of its in-corpus
    /// items were the automated market-wrap template, and The Globe and Mail is <c>Mill</c> because its
    /// in-corpus feed was entirely syndicated.
    /// </para>
    /// </summary>
    public static AttentionSourceTierOptions Default { get; } = new()
    {
        UnknownWeight = 0.1,
        SourceTiers = new Dictionary<string, SourceTier>(StringComparer.OrdinalIgnoreCase)
        {
            // Paid / company-originated distribution: visibility the company itself bought or issued.
            ["Wire"] = new SourceTier
            {
                Weight = 0.05,
                Publishers = new[]
                {
                    "PR Newswire", "GlobeNewswire", "Business Wire", "TMX Newsfile", "ACCESS Newswire",
                    "NewMediaWire",
                },
            },
            ["Mill"] = new SourceTier
            {
                Weight = 0.1,
                Publishers = new[]
                {
                    // Spec 88/90 seed + long-tail expansion.
                    "MarketBeat", "Zacks", "Simply Wall St", "StockStory", "Moomoo", "TradingView",
                    "Stock Titan", "GuruFocus", "Defense World", "Pluang", "MarketScreener",
                    "Finviz", "Investing.com", "Insider Monkey", "Benzinga", "TipRanks", "StockAnalysis",
                    "Simplywall.st",

                    // Spec 196 §2, from the sampled audit: aggregators and templated per-ticker outlets.
                    "Yahoo Finance", "Quiver Quantitative", "Sahm", "vinanet.vn", "Kalkine Media",
                    "The Globe and Mail", "Revelio Labs", "TradingKey", "Eastern Progress", "CryptoRank",
                    "MarketWatch", "AlphaStreet", "Barchart.com", "StocksToTrade", "AOL.com", "KING5.com",
                    "Trefis", "timothysykes.com", "Caledonian Record", "ChartMill", "The Manila Times",
                    "Zacks Investment Research",

                    // Name variants and regional editions of the outlets above. Explicit ALIASES, because
                    // normalization cannot bridge an appended edition word ("Investing.com Nigeria" →
                    // investingcomnigeria) and broadening Normalize with a prefix rule would risk silently
                    // collapsing unrelated outlets.
                    "Investing.com Nigeria", "Investing.com Canada", "Investing.com South Africa",
                    "Investing.com India", "Investing.com Australia",
                    "Yahoo", "Yahoo Finance UK", "Yahoo Finance Singapore", "Yahoo! Finance Canada",
                    "Yahoo Sports", "Yahoo Tech",
                },
            },
            // Contributor investor content: a human chose this company; the outlet gatekeeps little.
            ["Platform"] = new SourceTier
            {
                Weight = 0.3,
                Publishers = new[]
                {
                    "Seeking Alpha", "The Motley Fool", "24/7 Wall St.", "Morningstar", "Nareit",
                },
            },
            ["Genuine"] = new SourceTier
            {
                Weight = 1.0,
                Publishers = new[]
                {
                    "Reuters", "Bloomberg", "The Wall Street Journal", "CNBC", "Associated Press",
                    "Financial Times", "SpaceNews",

                    // Spec 196 §2: original reporting with clear editorial selection, plus a name variant
                    // of the already-listed The Wall Street Journal.
                    "The Business Journals", "WSJ",
                },
            },
        },
    };
}
