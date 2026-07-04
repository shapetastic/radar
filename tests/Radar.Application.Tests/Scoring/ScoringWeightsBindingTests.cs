using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Radar.Application.Scoring;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Application.Tests.Scoring;

public sealed class ScoringWeightsBindingTests
{
    private static ScoringWeights Resolve(IEnumerable<KeyValuePair<string, string?>> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddRadarScoringWeights(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ScoringWeights>();
    }

    [Fact]
    public void BlankProfile_ResolvesCodeDefaults()
    {
        var weights = Resolve(new Dictionary<string, string?>());

        Assert.Equal(new ScoringWeights(), weights);
    }

    [Fact]
    public void AbsentDefaultProfile_ResolvesCodeDefaults()
    {
        var weights = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Scoring:Profile"] = "default",
        });

        Assert.Equal(new ScoringWeights(), weights);
    }

    [Fact]
    public void NamedProfile_OverridesOnlyItsSpecifiedFields()
    {
        var weights = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Scoring:Profile"] = "aggressive-attention",
            ["Radar:Scoring:Profiles:aggressive-attention:AttentionHalfSaturation"] = "1.5",
            ["Radar:Scoring:Profiles:aggressive-attention:OpportunityAttentionDivisor"] = "300.0",
        });

        Assert.Equal(1.5, weights.AttentionHalfSaturation);
        Assert.Equal(300.0, weights.OpportunityAttentionDivisor);
        // Unspecified fields keep the code defaults (== v4).
        var defaults = new ScoringWeights();
        Assert.Equal(defaults.MediaReachWeight, weights.MediaReachWeight);
        Assert.Equal(defaults.QualityHigh, weights.QualityHigh);
        Assert.Equal(defaults.DiversityTarget, weights.DiversityTarget);
    }

    [Fact]
    public void RequestedButMissingNamedProfile_FailsFast()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Radar:Scoring:Profile"] = "does-not-exist",
            })
            .Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddRadarScoringWeights(configuration));
    }

    [Fact]
    public void InvalidWeight_FailsFastAtRegistration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Radar:Scoring:Profile"] = "broken",
                ["Radar:Scoring:Profiles:broken:OpportunityAttentionDivisor"] = "0",
            })
            .Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddRadarScoringWeights(configuration));
    }

    [Fact]
    public void EmptyNamedDefaultProfile_ResolvesCodeDefaults()
    {
        // A present-but-empty "default" profile binds nothing → all code defaults (byte-identical v4).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Give the section existence via an unrelated marker is not possible without a key; instead
                // bind one field to its default so the section exists yet output equals defaults.
                ["Radar:Scoring:Profiles:default:RecencyFloor"] = "0.5",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddRadarScoringWeights(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(new ScoringWeights(), provider.GetRequiredService<ScoringWeights>());
    }
}
