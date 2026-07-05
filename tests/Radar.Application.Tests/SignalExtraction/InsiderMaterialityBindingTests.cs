using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Application.Tests.SignalExtraction;

public sealed class InsiderMaterialityBindingTests
{
    private static InsiderMaterialityWeights Resolve(IEnumerable<KeyValuePair<string, string?>> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddRadarInsiderMateriality(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<InsiderMaterialityWeights>();
    }

    [Fact]
    public void BlankProfile_ResolvesCodeDefaults()
    {
        var weights = Resolve(new Dictionary<string, string?>());

        Assert.Equal(new InsiderMaterialityWeights().CanonicalDescriptor(), weights.CanonicalDescriptor());
    }

    [Fact]
    public void AbsentDefaultProfile_ResolvesCodeDefaults()
    {
        var weights = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Insider:Profile"] = "default",
        });

        Assert.Equal(new InsiderMaterialityWeights().CanonicalDescriptor(), weights.CanonicalDescriptor());
    }

    [Fact]
    public void NamedProfile_AppliesDeltaOntoDefaults()
    {
        // Override only the cluster boost and the buy tiers; the sell tiers keep the code default (== spec 93).
        var weights = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Insider:Profile"] = "buy-tilt",
            ["Radar:Insider:Profiles:buy-tilt:ClusterBoost"] = "2",
            ["Radar:Insider:Profiles:buy-tilt:BuyTiers:0:MinInclusive"] = "250000",
            ["Radar:Insider:Profiles:buy-tilt:BuyTiers:0:Strength"] = "9",
            ["Radar:Insider:Profiles:buy-tilt:BuyTiers:1:MinInclusive"] = "-79228162514264337593543950335",
            ["Radar:Insider:Profiles:buy-tilt:BuyTiers:1:Strength"] = "3",
        });

        Assert.Equal(2, weights.ClusterBoost);

        Assert.Equal(2, weights.BuyTiers.Count);
        Assert.Equal(250_000m, weights.BuyTiers[0].MinInclusive);
        Assert.Equal(9, weights.BuyTiers[0].Strength);
        Assert.Equal(decimal.MinValue, weights.BuyTiers[1].MinInclusive);
        Assert.Equal(3, weights.BuyTiers[1].Strength);

        // Unspecified sell tiers keep the code default (spec 93).
        Assert.Equal(new InsiderMaterialityWeights().SellTiers.Count, weights.SellTiers.Count);
        Assert.Equal(8, weights.SellTiers[0].Strength);
    }

    [Fact]
    public void RequestedButMissingNamedProfile_FailsFast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Radar:Insider:Profile"] = "does-not-exist",
            })
            .Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddRadarInsiderMateriality(configuration));
    }

    [Fact]
    public void InvalidTier_FailsFastAtRegistration()
    {
        // A Strength of 99 is outside the domain range 1..10 → Validate() throws at registration.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Radar:Insider:Profile"] = "broken",
                ["Radar:Insider:Profiles:broken:BuyTiers:0:MinInclusive"] = "1000000",
                ["Radar:Insider:Profiles:broken:BuyTiers:0:Strength"] = "99",
                ["Radar:Insider:Profiles:broken:BuyTiers:1:MinInclusive"] = "-79228162514264337593543950335",
                ["Radar:Insider:Profiles:broken:BuyTiers:1:Strength"] = "2",
            })
            .Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddRadarInsiderMateriality(configuration));
    }
}
