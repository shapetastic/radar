using Radar.Infrastructure.Attention;

namespace Radar.Infrastructure.Tests.Attention;

public sealed class ConfiguredAttentionSourceWeightsTests
{
    private static AttentionSourceTierOptions Options(
        double unknown = 0.5, double mill = 0.1, double genuine = 1.0) => new()
    {
        UnknownWeight = unknown,
        SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mill"] = new AttentionSourceTierOptions.SourceTier
            {
                Weight = mill,
                Publishers = new[] { "MarketBeat", "Simply Wall St" },
            },
            ["Genuine"] = new AttentionSourceTierOptions.SourceTier
            {
                Weight = genuine,
                Publishers = new[] { "Reuters" },
            },
        },
    };

    [Fact]
    public void WeightFor_ListedPublisher_ReturnsTierWeight()
    {
        var weights = new ConfiguredAttentionSourceWeights(Options());

        Assert.Equal(0.1, weights.WeightFor("MarketBeat"));
        Assert.Equal(1.0, weights.WeightFor("Reuters"));
    }

    [Fact]
    public void WeightFor_UnlistedPublisher_ReturnsUnknownWeight()
    {
        var weights = new ConfiguredAttentionSourceWeights(Options(unknown: 0.5));

        Assert.Equal(0.5, weights.WeightFor("Some Niche Trade Journal"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WeightFor_BlankOrNull_ReturnsUnknownWeight(string? name)
    {
        var weights = new ConfiguredAttentionSourceWeights(Options(unknown: 0.4));

        Assert.Equal(0.4, weights.WeightFor(name));
    }

    [Fact]
    public void WeightFor_IsCaseInsensitive_AndWhitespaceTolerant()
    {
        var weights = new ConfiguredAttentionSourceWeights(Options());

        Assert.Equal(0.1, weights.WeightFor("marketbeat"));
        Assert.Equal(0.1, weights.WeightFor("  MARKETBEAT  "));
        // Collapsing internal whitespace runs: "Simply  Wall  St" resolves to the listed "Simply Wall St".
        Assert.Equal(0.1, weights.WeightFor("Simply  Wall  St"));
    }

    [Fact]
    public void Default_TiersSensibly_WithoutConfig()
    {
        var weights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        Assert.Equal(0.1, weights.WeightFor("Zacks"));
        Assert.Equal(1.0, weights.WeightFor("Bloomberg"));
        // Newly-listed long-tail aggregators tier as mill (spec 90 denylist-expand).
        Assert.Equal(0.1, weights.WeightFor("Finviz"));
        Assert.Equal(0.1, weights.WeightFor("Investing.com"));
        // A truly-unlisted publisher falls to the recalibrated 0.25 unknown default (was 0.5).
        Assert.Equal(0.25, weights.WeightFor("Definitely Not Listed"));
    }

    [Fact]
    public void WeightFor_DomainFormVariants_ResolveToMillWeight()
    {
        // Observed Google-News domain-form variants normalize onto their curated mill entries: a trailing
        // common-TLD token is stripped and punctuation/case is removed, so ".com" forms match the bare outlet
        // and the explicit "Simplywall.st" alias covers the word-eliding domain.
        var weights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        Assert.Equal(0.1, weights.WeightFor("marketscreener.com"));
        Assert.Equal(0.1, weights.WeightFor("MarketScreener.COM"));
        Assert.Equal(0.1, weights.WeightFor("marketbeat.com"));
        Assert.Equal(0.1, weights.WeightFor("simplywall.st"));
        Assert.Equal(0.1, weights.WeightFor("finviz.com"));
        Assert.Equal(0.1, weights.WeightFor("investing.com"));
    }

    [Fact]
    public void WeightFor_DistinctOutlets_DoNotCollapse()
    {
        // Non-collision guard: conservative normalization (no fuzzy/vowel stripping) must not over-collapse two
        // genuinely-distinct names. A listed genuine outlet keeps its weight while a made-up outlet that merely
        // shares a prefix falls to the unknown default — the two are not equal.
        var weights = new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default);

        var reuters = weights.WeightFor("Reuters");
        var fake = weights.WeightFor("Reuters Breakingviews Fake");

        Assert.Equal(1.0, reuters);
        Assert.Equal(0.25, fake);
        Assert.NotEqual(reuters, fake);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfiguredAttentionSourceWeights(null!));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Constructor_NegativeOrOverOneTierWeight_FailsFast(double badWeight)
    {
        var options = Options(mill: badWeight);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ConfiguredAttentionSourceWeights(options));
        Assert.Contains("Radar:Attention", ex.Message);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(2.0)]
    public void Constructor_UnknownWeightOutsideRange_FailsFast(double badUnknown)
    {
        var options = Options(unknown: badUnknown);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ConfiguredAttentionSourceWeights(options));
        Assert.Contains("Radar:Attention", ex.Message);
    }

    [Fact]
    public void CanonicalDescriptor_NormalizesReservedDelimitersOutOfKeys()
    {
        var options = new AttentionSourceTierOptions
        {
            UnknownWeight = 0.25,
            SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(StringComparer.OrdinalIgnoreCase)
            {
                ["Weird"] = new AttentionSourceTierOptions.SourceTier
                {
                    Weight = 0.25,
                    Publishers = new[] { "p=0.25;q", "100% News" },
                },
            },
        };

        var descriptor = new ConfiguredAttentionSourceWeights(options).CanonicalDescriptor();

        // Post-spec-90, Normalize strips ALL non-alphanumerics before a key can reach the descriptor, so the
        // reserved delimiters (= ; %) can never appear in a key: "p=0.25;q" → "p025q" and "100% News" → "100news".
        // The normalized keys are present; the raw/escaped delimiter forms are not.
        Assert.Contains("p025q=0.25;", descriptor);
        Assert.Contains("100news=0.25;", descriptor);
        Assert.DoesNotContain("%3D", descriptor);
        Assert.DoesNotContain("%25", descriptor);
    }

    [Fact]
    public void CanonicalDescriptor_DistinctTierMapsThatWouldCollide_ProduceDistinctDescriptors()
    {
        // Post-spec-90 the injectivity comes from normalization stripping the reserved delimiters out of the
        // key: a single publisher literally named "p=0.25;q" normalizes to the key "p025q", whereas two separate
        // publishers "p" and "q" normalize to keys "p" and "q" — distinct keys, so the descriptors still differ.
        var single = new AttentionSourceTierOptions
        {
            UnknownWeight = 0.5,
            SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(StringComparer.OrdinalIgnoreCase)
            {
                ["T"] = new AttentionSourceTierOptions.SourceTier
                {
                    Weight = 0.25,
                    Publishers = new[] { "p=0.25;q" },
                },
            },
        };
        var pair = new AttentionSourceTierOptions
        {
            UnknownWeight = 0.5,
            SourceTiers = new Dictionary<string, AttentionSourceTierOptions.SourceTier>(StringComparer.OrdinalIgnoreCase)
            {
                ["T"] = new AttentionSourceTierOptions.SourceTier
                {
                    Weight = 0.25,
                    Publishers = new[] { "p", "q" },
                },
            },
        };

        var singleDescriptor = new ConfiguredAttentionSourceWeights(single).CanonicalDescriptor();
        var pairDescriptor = new ConfiguredAttentionSourceWeights(pair).CanonicalDescriptor();

        Assert.NotEqual(pairDescriptor, singleDescriptor);
    }
}
