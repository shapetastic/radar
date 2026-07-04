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
        Assert.Equal(0.5, weights.WeightFor("Definitely Not Listed"));
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
    public void CanonicalDescriptor_EscapesReservedDelimitersInPublisherKeys()
    {
        var options = new AttentionSourceTierOptions
        {
            UnknownWeight = 0.5,
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

        // The reserved delimiters (= ; %) are percent-escaped inside the key so they cannot be mistaken for
        // structural separators. The escaped forms are present; the literals only ever appear as separators.
        Assert.Contains("p%3D0.25%3Bq=0.25;", descriptor);
        Assert.Contains("100%25 News=0.25;", descriptor);
    }

    [Fact]
    public void CanonicalDescriptor_DistinctTierMapsThatWouldCollide_ProduceDistinctDescriptors()
    {
        // Without escaping, a single publisher literally named "p=0.25;q" serializes to the same bytes as two
        // separate publishers "p" and "q" (both weight 0.25) — a non-injective fingerprint input. Escaping the
        // reserved delimiters keeps the two maps distinguishable.
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
