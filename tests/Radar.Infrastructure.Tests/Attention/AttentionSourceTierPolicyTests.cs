using Radar.Application.Scoring;
using Radar.Infrastructure.Attention;

namespace Radar.Infrastructure.Tests.Attention;

/// <summary>
/// Spec 196 §6 — the inverted default, the four-tier policy, the alias/normalization cases, the
/// fail-fast on an ambiguous publisher, and the DGII regression that motivated the slice.
/// <para>
/// The tier WEIGHTS asserted here are the spec's decision (Wire 0.05 / Mill 0.1 / Platform 0.3 /
/// Genuine 1.0); the MEMBERSHIP is the committed audit's
/// (<c>docs/cohorts/attention-publisher-audit-v1.md</c>). Neither is re-derived here — these tests pin that
/// the shipped table matches what was decided and audited.
/// </para>
/// </summary>
public sealed class AttentionSourceTierPolicyTests
{
    private static ConfiguredAttentionSourceWeights Default() =>
        new(AttentionSourceTierOptions.Default);

    // ---------------------------------------------------------------------------------------------
    // §1 — the inversion: unknown is not notice
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownPublisher_WeightsOneTenth_NotAQuarter()
    {
        // The whole point of §1: an unrecognised publisher is low-signal coverage, not quarter-strength
        // genuine notice. Non-zero, deliberately — a real outlet not yet on the list is under-counted, never
        // silently erased.
        var weights = Default();

        Assert.Equal(0.1, weights.WeightFor("A Publisher Nobody Has Classified"));
        Assert.Equal(0.1, AttentionSourceTierOptions.Default.UnknownWeight);
        Assert.True(AttentionSourceTierOptions.Default.UnknownWeight > 0.0);
    }

    [Fact]
    public void UnknownWeight_StaysConfigurable_AndAConfiguredOverrideWins()
    {
        // The inversion is a DECLARED DEFAULT, not a hard-coded belief: a composition that binds
        // Radar:Attention:UnknownWeight still gets exactly what it asked for.
        var overridden = new ConfiguredAttentionSourceWeights(new AttentionSourceTierOptions
        {
            UnknownWeight = 0.42,
            SourceTiers = AttentionSourceTierOptions.Default.SourceTiers,
        });

        Assert.Equal(0.42, overridden.WeightFor("A Publisher Nobody Has Classified"));
        // …and an explicitly-mapped publisher is untouched by that override.
        Assert.Equal(1.0, overridden.WeightFor("Reuters"));
    }

    // ---------------------------------------------------------------------------------------------
    // §2 — the four tiers, and the audited membership
    // ---------------------------------------------------------------------------------------------

    [Theory]
    // Wire — paid or company-originated distribution.
    [InlineData("PR Newswire", "Wire", 0.05)]
    [InlineData("GlobeNewswire", "Wire", 0.05)]
    [InlineData("Business Wire", "Wire", 0.05)]
    [InlineData("TMX Newsfile", "Wire", 0.05)]
    [InlineData("ACCESS Newswire", "Wire", 0.05)]
    [InlineData("NewMediaWire", "Wire", 0.05)]
    // Mill — no demonstrated independent selection.
    [InlineData("Yahoo Finance", "Mill", 0.1)]
    [InlineData("Quiver Quantitative", "Mill", 0.1)]
    [InlineData("Sahm", "Mill", 0.1)]
    [InlineData("vinanet.vn", "Mill", 0.1)]
    [InlineData("Kalkine Media", "Mill", 0.1)]
    [InlineData("The Globe and Mail", "Mill", 0.1)]
    [InlineData("Revelio Labs", "Mill", 0.1)]
    [InlineData("TradingKey", "Mill", 0.1)]
    [InlineData("Eastern Progress", "Mill", 0.1)]
    [InlineData("CryptoRank", "Mill", 0.1)]
    // AUDIT OVERRODE REPUTATION: all ten in-corpus MarketWatch items were the automated
    // "stock outperforms/underperforms competitors" market-wrap template.
    [InlineData("MarketWatch", "Mill", 0.1)]
    [InlineData("AlphaStreet", "Mill", 0.1)]
    [InlineData("Barchart.com", "Mill", 0.1)]
    [InlineData("StocksToTrade", "Mill", 0.1)]
    [InlineData("AOL.com", "Mill", 0.1)]
    [InlineData("KING5.com", "Mill", 0.1)]
    [InlineData("Trefis", "Mill", 0.1)]
    [InlineData("timothysykes.com", "Mill", 0.1)]
    [InlineData("Caledonian Record", "Mill", 0.1)]
    [InlineData("ChartMill", "Mill", 0.1)]
    [InlineData("The Manila Times", "Mill", 0.1)]
    [InlineData("Zacks Investment Research", "Mill", 0.1)]
    [InlineData("Yahoo", "Mill", 0.1)]
    [InlineData("Yahoo Finance UK", "Mill", 0.1)]
    [InlineData("Yahoo Finance Singapore", "Mill", 0.1)]
    [InlineData("Yahoo! Finance Canada", "Mill", 0.1)]
    [InlineData("Yahoo Sports", "Mill", 0.1)]
    [InlineData("Yahoo Tech", "Mill", 0.1)]
    [InlineData("Investing.com Canada", "Mill", 0.1)]
    [InlineData("Investing.com South Africa", "Mill", 0.1)]
    [InlineData("Investing.com India", "Mill", 0.1)]
    [InlineData("Investing.com Australia", "Mill", 0.1)]
    // Platform — contributor analysis with weak gatekeeping.
    [InlineData("Seeking Alpha", "Platform", 0.3)]
    [InlineData("The Motley Fool", "Platform", 0.3)]
    [InlineData("24/7 Wall St.", "Platform", 0.3)]
    [InlineData("Morningstar", "Platform", 0.3)]
    [InlineData("Nareit", "Platform", 0.3)]
    // Genuine — independent reporting / editorial selection.
    [InlineData("The Business Journals", "Genuine", 1.0)]
    [InlineData("WSJ", "Genuine", 1.0)]
    [InlineData("Reuters", "Genuine", 1.0)]
    public void EachAuditedPublisher_ResolvesToItsIntendedTier(
        string publisher, string tier, double weight)
    {
        var resolution = Default().Resolve(publisher);

        Assert.True(resolution.IsExplicitlyMapped, $"'{publisher}' must be explicitly classified");
        Assert.Equal(tier, resolution.TierName);
        Assert.Equal(weight, resolution.Weight);
    }

    [Fact]
    public void SeekingAlphaAndTheMotleyFool_ShareOneTier()
    {
        // The first draft split two broadly comparable investor-content platforms tenfold with no stated
        // principle. The tier definition — "a human chose this company, the outlet gatekeeps little" —
        // covers both, so they must land together.
        var weights = Default();

        Assert.Equal("Platform", weights.Resolve("Seeking Alpha").TierName);
        Assert.Equal("Platform", weights.Resolve("The Motley Fool").TierName);
        Assert.Equal(weights.WeightFor("Seeking Alpha"), weights.WeightFor("The Motley Fool"));
    }

    [Fact]
    public void WireTier_IsStrictlyBelowMill()
    {
        // A press release confers visibility the company itself bought; a content mill at least chose to
        // publish something. The ordering is the policy, so it is asserted rather than assumed.
        var weights = Default();

        Assert.True(
            weights.WeightFor("PR Newswire") < weights.WeightFor("MarketBeat"),
            "Wire must weigh strictly less than Mill");
        Assert.True(weights.WeightFor("MarketBeat") < weights.WeightFor("Seeking Alpha"));
        Assert.True(weights.WeightFor("Seeking Alpha") < weights.WeightFor("Reuters"));
    }

    // ---------------------------------------------------------------------------------------------
    // §2 — matching: one real fix (the alias), one retraction (marketscreener was never broken)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void MarketScreenerDotCom_StillResolvesByNormalization_NotByAnAlias()
    {
        // RETRACTION GUARD, not a fix. The first draft claimed the domain form did not match; it always
        // did, via the trailing-TLD strip. This test exists so a future change to Normalize cannot silently
        // break it — note there is no "marketscreener.com" entry in the tier list to prop it up.
        var resolution = Default().Resolve("marketscreener.com");

        Assert.True(resolution.IsExplicitlyMapped);
        Assert.Equal("Mill", resolution.TierName);
        Assert.Equal("marketscreener", resolution.NormalizedPublisher);
        Assert.DoesNotContain(
            AttentionSourceTierOptions.Default.SourceTiers["Mill"].Publishers,
            p => string.Equals(p, "marketscreener.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvestingComNigeria_ResolvesByItsExplicitAlias_BecauseNormalizationCannotBridgeIt()
    {
        // The REAL matching gap: the regional edition normalizes to "investingcomnigeria", which shares no
        // key with the listed "Investing.com" ("investing" — the TLD strip removes ".com"). An alias entry
        // fixes it WITHOUT broadening Normalize, so a prefix rule can never silently collapse unrelated
        // outlets.
        var weights = Default();
        var nigeria = weights.Resolve("Investing.com Nigeria");
        var parent = weights.Resolve("Investing.com");

        Assert.Equal("investingcomnigeria", nigeria.NormalizedPublisher);
        Assert.Equal("investing", parent.NormalizedPublisher);
        Assert.NotEqual(parent.NormalizedPublisher, nigeria.NormalizedPublisher);
        Assert.True(nigeria.IsExplicitlyMapped);
        Assert.Equal("Mill", nigeria.TierName);
    }

    [Fact]
    public void AnUnrelatedPublisher_DoesNotCollideWithAClassifiedFamily()
    {
        // Non-collision guard for the families spec 196 added: neither an invented "Yahoo"-adjacent outlet
        // nor an invented "Investing"-adjacent one may inherit a classification it was never audited for.
        var weights = Default();

        var yahooLike = weights.Resolve("Yahoo Valley Independent Press");
        var investingLike = weights.Resolve("Investing Club of Ohio");
        var wireLike = weights.Resolve("Business Wireless Weekly");

        Assert.False(yahooLike.IsExplicitlyMapped);
        Assert.False(investingLike.IsExplicitlyMapped);
        Assert.False(wireLike.IsExplicitlyMapped);
        Assert.Equal(AttentionSourceResolution.UnclassifiedTierName, yahooLike.TierName);
        Assert.Equal(0.1, wireLike.Weight);
        Assert.NotEqual(weights.WeightFor("Business Wire"), wireLike.Weight);
    }

    // ---------------------------------------------------------------------------------------------
    // §3 — the typed resolver
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_DistinguishesAnExplicitMill_FromAnUnclassifiedPublisher_DespiteTheSharedWeight()
    {
        // THE reason the resolver exists. Since §1 inverted the default these two return the same NUMBER,
        // so a diagnostic built on WeightFor would report every audited mill as unclassified and lie about
        // the very gap it exists to expose.
        var weights = Default();
        var mill = weights.Resolve("Yahoo Finance");
        var unknown = weights.Resolve("Some Outlet Nobody Audited");

        Assert.Equal(mill.Weight, unknown.Weight);
        Assert.True(mill.IsExplicitlyMapped);
        Assert.False(unknown.IsExplicitlyMapped);
        Assert.Equal("Mill", mill.TierName);
        Assert.Equal(AttentionSourceResolution.UnclassifiedTierName, unknown.TierName);
    }

    [Fact]
    public void WeightFor_IsExactlyResolveWeight_ForEveryListedPublisherAndForTheUnknownDefault()
    {
        // "One matching implementation, two consumers": if these ever disagreed, the score and the
        // diagnostic would be describing different maps.
        var weights = Default();
        var names = AttentionSourceTierOptions.Default.SourceTiers
            .SelectMany(t => t.Value.Publishers)
            .Concat(["Some Outlet Nobody Audited", "", "   "])
            .ToArray();

        Assert.All(names, n => Assert.Equal(weights.Resolve(n).Weight, weights.WeightFor(n)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankOrNull_IsUnclassified_WithAnEmptyNormalizedKey(string? name)
    {
        var resolution = Default().Resolve(name);

        Assert.False(resolution.IsExplicitlyMapped);
        Assert.Equal(string.Empty, resolution.NormalizedPublisher);
        Assert.Equal(0.1, resolution.Weight);
    }

    [Fact]
    public void Constructor_PublisherClaimedByTwoTiers_ThrowsNamingBoth()
    {
        // Fail fast rather than ordinal last-wins: with named tiers, silently taking "whichever sorted
        // last" would make both the score and the diagnostic depend on tier-NAME ordering.
        var options = new AttentionSourceTierOptions
        {
            UnknownWeight = 0.1,
            SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Mill"] = new AttentionSourceTierOptions.SourceTier
                {
                    Weight = 0.1,
                    Publishers = new[] { "Seeking Alpha" },
                },
                ["Platform"] = new AttentionSourceTierOptions.SourceTier
                {
                    Weight = 0.3,
                    Publishers = new[] { "seekingalpha.com" },
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ConfiguredAttentionSourceWeights(options));

        Assert.Contains("seekingalpha", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Mill'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Platform'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one tier", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_SamePublisherTwiceInOneTier_IsIdempotent_AndDoesNotThrow()
    {
        // A duplicate entry, or two spellings that normalize alike, names ONE tier — nothing is ambiguous.
        var options = new AttentionSourceTierOptions
        {
            UnknownWeight = 0.1,
            SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Mill"] = new AttentionSourceTierOptions.SourceTier
                {
                    Weight = 0.1,
                    Publishers = new[] { "Seeking Alpha", "seekingalpha.com", "SEEKING ALPHA" },
                },
            },
        };

        var weights = new ConfiguredAttentionSourceWeights(options);

        Assert.Equal("Mill", weights.Resolve("Seeking Alpha").TierName);
    }

    [Fact]
    public void TheShippedDefault_Constructs_SoNoPublisherIsClaimedTwice()
    {
        // The ambiguity guard applied to the table this slice actually ships: a curation mistake that put
        // one outlet in two tiers would fail here rather than in a live run.
        var ex = Record.Exception(() => new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default));

        Assert.Null(ex);
    }

    // ---------------------------------------------------------------------------------------------
    // §6 — the descriptor stays deterministic, and its SHAPE is unchanged
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CanonicalDescriptor_IsUnchangedByTierOrdering_AndByPublisherOrdering()
    {
        var a = new ConfiguredAttentionSourceWeights(Reordered(reverseTiers: false)).CanonicalDescriptor();
        var b = new ConfiguredAttentionSourceWeights(Reordered(reverseTiers: true)).CanonicalDescriptor();

        Assert.Equal(a, b);

        static AttentionSourceTierOptions Reordered(bool reverseTiers)
        {
            // Same membership and same weights, presented in the opposite tier order with each tier's
            // publisher list reversed. A descriptor that depended on either would be a non-deterministic
            // fingerprint input (AD-3).
            var entries = AttentionSourceTierOptions.Default.SourceTiers.ToList();
            if (reverseTiers)
            {
                entries.Reverse();
            }

            var map = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (name, tier) in entries)
            {
                map[name] = new AttentionSourceTierOptions.SourceTier
                {
                    Weight = tier.Weight,
                    Publishers = reverseTiers ? [.. tier.Publishers.Reverse()] : tier.Publishers,
                };
            }

            return new AttentionSourceTierOptions
            {
                UnknownWeight = AttentionSourceTierOptions.Default.UnknownWeight,
                SourceTiers = map,
            };
        }
    }

    [Fact]
    public void CanonicalDescriptor_IsCultureInvariant()
    {
        var invariant = Default().CanonicalDescriptor();
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // de-DE renders 0.05 as "0,05" under a culture-sensitive formatter; the descriptor must not move.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(invariant, Default().CanonicalDescriptor());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CanonicalDescriptor_CarriesTheUnknownDefaultAndWeights_ButNotTierNames()
    {
        // The SHAPE is deliberately unchanged by spec 196's named tiers: a tier RENAME with identical
        // weights and identical membership produces identical scoring, so it must not re-stamp a series.
        var descriptor = Default().CanonicalDescriptor();

        Assert.StartsWith("unknown=0.1;", descriptor, StringComparison.Ordinal);
        Assert.Contains("seekingalpha=0.3;", descriptor, StringComparison.Ordinal);
        Assert.Contains("prnewswire=0.05;", descriptor, StringComparison.Ordinal);
        Assert.DoesNotContain("Platform", descriptor, StringComparison.Ordinal);
        Assert.DoesNotContain("Wire", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalDescriptor_MovesWhenTheMapMoves_AndNotWhenOnlyATierNameDoes()
    {
        var renamed = new AttentionSourceTierOptions
        {
            UnknownWeight = AttentionSourceTierOptions.Default.UnknownWeight,
            SourceTiers = AttentionSourceTierOptions.Default.SourceTiers.ToDictionary(
                t => t.Key == "Platform" ? "InvestorPlatform" : t.Key,
                t => t.Value,
                StringComparer.OrdinalIgnoreCase),
        };
        var remembered = new AttentionSourceTierOptions
        {
            UnknownWeight = 0.25,
            SourceTiers = AttentionSourceTierOptions.Default.SourceTiers,
        };

        Assert.Equal(
            Default().CanonicalDescriptor(),
            new ConfiguredAttentionSourceWeights(renamed).CanonicalDescriptor());
        Assert.NotEqual(
            Default().CanonicalDescriptor(),
            new ConfiguredAttentionSourceWeights(remembered).CanonicalDescriptor());
    }

    // ---------------------------------------------------------------------------------------------
    // §6 — the DGII regression: direction and MECHANISM, never a magic number
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// DGII (Digi International) is the worked example that prompted spec 196: it scored attention 75 while
    /// its largest single source was Yahoo Finance — unclassified, therefore weighted 0.25, two and a half
    /// times a Mill publisher — and the maintainer, an engaged investor, had never heard of the company.
    /// <para>
    /// These are the seventeen distinct in-window publishers measured for DGII in the audited corpus, which
    /// is the unit <c>AttentionReach</c> actually consumes (a publisher appearing forty times counts once).
    /// </para>
    /// </summary>
    private static readonly string[] DigiInternationalPublishers =
    [
        // Classified as Mill before spec 196 (8).
        "MarketBeat", "Simplywall.st", "Stock Titan", "Moomoo", "GuruFocus", "TradingView", "Pluang", "Zacks",
        // Newly classified by the spec-196 audit (6).
        "Yahoo Finance", "Revelio Labs", "vinanet.vn", "AlphaStreet",   // → Mill
        "Seeking Alpha",                                                // → Platform
        "GlobeNewswire",                                                // → Wire
        // Still unclassified after the audit (3) — the long tail the inversion exists to price honestly.
        "Digi International", "IoT Business News", "The Bakersfield Californian",
    ];

    private static AttentionSourceTierOptions PreSpec196TierMap() => new()
    {
        UnknownWeight = 0.25,
        SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Mill"] = new AttentionSourceTierOptions.SourceTier
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
            ["Genuine"] = new AttentionSourceTierOptions.SourceTier
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

    [Fact]
    public void Dgii_TierWeightedReach_FallsMaterially_AndTheMechanismIsTheAuditedReclassification()
    {
        // Direction AND mechanism, never a magic number: the expected values are derived FROM the resolver
        // under test, so this cannot drift into asserting a transcribed constant.
        var before = new ConfiguredAttentionSourceWeights(PreSpec196TierMap());
        var after = Default();

        var reachBefore = ScoreSignalMath.TierWeightedReach(DigiInternationalPublishers, before);
        var reachAfter = ScoreSignalMath.TierWeightedReach(DigiInternationalPublishers, after);

        // DIRECTION: DGII's tier-weighted distinct-publisher reach falls materially.
        Assert.True(
            reachAfter < reachBefore,
            $"DGII reach must fall: was {reachBefore}, now {reachAfter}");
        Assert.True(
            reachAfter < reachBefore * 0.75,
            $"the fall must be material, not marginal: was {reachBefore}, now {reachAfter}");

        // MECHANISM 1 — its dominant sources move OFF the unclassified 0.25 and onto audited tiers.
        foreach (var publisher in new[] { "Yahoo Finance", "Revelio Labs", "vinanet.vn", "AlphaStreet" })
        {
            Assert.False(before.Resolve(publisher).IsExplicitlyMapped);
            Assert.Equal(0.25, before.WeightFor(publisher));
            Assert.Equal("Mill", after.Resolve(publisher).TierName);
            Assert.Equal(0.1, after.WeightFor(publisher));
        }

        Assert.False(before.Resolve("Seeking Alpha").IsExplicitlyMapped);
        Assert.Equal("Platform", after.Resolve("Seeking Alpha").TierName);
        Assert.Equal(0.3, after.WeightFor("Seeking Alpha"));

        Assert.False(before.Resolve("GlobeNewswire").IsExplicitlyMapped);
        Assert.Equal("Wire", after.Resolve("GlobeNewswire").TierName);

        // MECHANISM 2 — the residual tail is repriced by the INVERSION alone, not by classification.
        foreach (var publisher in new[]
                 { "Digi International", "IoT Business News", "The Bakersfield Californian" })
        {
            Assert.False(before.Resolve(publisher).IsExplicitlyMapped);
            Assert.False(after.Resolve(publisher).IsExplicitlyMapped);
            Assert.Equal(0.25, before.WeightFor(publisher));
            Assert.Equal(0.1, after.WeightFor(publisher));
        }

        // MECHANISM 3 — the already-audited mills did NOT move, so the fall is attributable to the two
        // mechanisms above rather than to a quiet re-tuning of the Mill weight.
        foreach (var publisher in new[] { "MarketBeat", "Stock Titan", "GuruFocus", "TradingView", "Pluang" })
        {
            Assert.Equal(before.WeightFor(publisher), after.WeightFor(publisher));
        }

        // …and the whole reach is exactly the sum of the per-publisher weights the resolver reports, so the
        // arithmetic above has no hidden term.
        Assert.Equal(DigiInternationalPublishers.Sum(after.WeightFor), reachAfter);
    }
}
