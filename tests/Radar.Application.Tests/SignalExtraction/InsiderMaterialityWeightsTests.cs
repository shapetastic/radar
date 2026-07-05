using System.Globalization;

using Radar.Application.SignalExtraction;

namespace Radar.Application.Tests.SignalExtraction;

public sealed class InsiderMaterialityWeightsTests
{
    [Fact]
    public void Defaults_ReproduceSpec93Table()
    {
        var weights = new InsiderMaterialityWeights();

        var expected = new (decimal MinInclusive, int Strength)[]
        {
            (5_000_000m, 8),
            (1_000_000m, 7),
            (250_000m, 6),
            (50_000m, 4),
            (decimal.MinValue, 2),
        };

        AssertTable(expected, weights.BuyTiers);
        AssertTable(expected, weights.SellTiers);
        Assert.Equal(1, weights.ClusterBoost);
    }

    private static void AssertTable(
        (decimal MinInclusive, int Strength)[] expected, IReadOnlyList<InsiderMaterialityTier> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].MinInclusive, actual[i].MinInclusive);
            Assert.Equal(expected[i].Strength, actual[i].Strength);
        }
    }

    [Fact]
    public void Validate_Default_DoesNotThrow()
    {
        new InsiderMaterialityWeights().Validate();
    }

    [Fact]
    public void Validate_EmptyTierList_Throws()
    {
        var weights = new InsiderMaterialityWeights { BuyTiers = [] };

        Assert.Throws<InvalidOperationException>(weights.Validate);
    }

    [Fact]
    public void Validate_MissingFloor_Throws()
    {
        // Lowest bound is not decimal.MinValue, so a small amount would map to nothing.
        var weights = new InsiderMaterialityWeights
        {
            SellTiers =
            [
                new(1_000_000m, 7),
                new(50_000m, 4),
            ],
        };

        Assert.Throws<InvalidOperationException>(weights.Validate);
    }

    [Fact]
    public void Validate_NonDescendingBounds_Throws()
    {
        var weights = new InsiderMaterialityWeights
        {
            BuyTiers =
            [
                new(50_000m, 4),
                new(1_000_000m, 7),
                new(decimal.MinValue, 2),
            ],
        };

        Assert.Throws<InvalidOperationException>(weights.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(99)]
    public void Validate_OutOfRangeStrength_Throws(int strength)
    {
        var weights = new InsiderMaterialityWeights
        {
            BuyTiers =
            [
                new(1_000_000m, strength),
                new(decimal.MinValue, 2),
            ],
        };

        Assert.Throws<InvalidOperationException>(weights.Validate);
    }

    [Fact]
    public void Validate_NonMonotonicStrength_Throws()
    {
        // A smaller amount ($50,000 -> 8) out-scores a larger amount ($1,000,000 -> 5) walking descending.
        var weights = new InsiderMaterialityWeights
        {
            SellTiers =
            [
                new(1_000_000m, 5),
                new(50_000m, 8),
                new(decimal.MinValue, 2),
            ],
        };

        Assert.Throws<InvalidOperationException>(weights.Validate);
    }

    [Fact]
    public void Validate_NegativeClusterBoost_Throws()
    {
        var weights = new InsiderMaterialityWeights { ClusterBoost = -1 };

        Assert.Throws<InvalidOperationException>(weights.Validate);
    }

    [Fact]
    public void CanonicalDescriptor_IsDeterministic()
    {
        var weights = new InsiderMaterialityWeights();

        Assert.Equal(weights.CanonicalDescriptor(), weights.CanonicalDescriptor());
    }

    [Fact]
    public void CanonicalDescriptor_Default_HasExpectedShape()
    {
        var descriptor = new InsiderMaterialityWeights().CanonicalDescriptor();

        var floor = decimal.MinValue.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(
            $"buy=5000000:8,1000000:7,250000:6,50000:4,{floor}:2;"
                + $"sell=5000000:8,1000000:7,250000:6,50000:4,{floor}:2;cluster=1;",
            descriptor);
    }

    [Fact]
    public void CanonicalDescriptor_IsCultureInvariant()
    {
        var weights = new InsiderMaterialityWeights();
        var invariant = weights.CanonicalDescriptor();

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, weights.CanonicalDescriptor());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CanonicalDescriptor_AsymmetricConfig_DiffersFromDefault()
    {
        var asymmetric = new InsiderMaterialityWeights
        {
            BuyTiers =
            [
                new(250_000m, 9),
                new(decimal.MinValue, 3),
            ],
            SellTiers =
            [
                new(250_000m, 4),
                new(decimal.MinValue, 1),
            ],
        };

        Assert.NotEqual(
            new InsiderMaterialityWeights().CanonicalDescriptor(),
            asymmetric.CanonicalDescriptor());

        // Buy and sell tables are serialized distinctly (the asymmetry survives serialization).
        Assert.Equal(
            "buy=250000:9,-79228162514264337593543950335:3;"
                + "sell=250000:4,-79228162514264337593543950335:1;cluster=1;",
            asymmetric.CanonicalDescriptor());
    }
}
