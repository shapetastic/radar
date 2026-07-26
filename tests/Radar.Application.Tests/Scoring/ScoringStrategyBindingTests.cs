using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Scoring;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 137 — <c>Radar:Strategies</c> / <c>Radar:PrimaryStrategy</c> binding and its fail-fast validation.
/// The load-bearing case is the FIRST one: with no strategies configured, composition must synthesise
/// exactly one primary strategy carrying the ambient <c>Radar:Scoring:Profile</c> weights, so behaviour and
/// the pinned default fingerprints are unmoved.
/// </summary>
public sealed class ScoringStrategyBindingTests
{
    private static ScoringStrategySet Resolve(IDictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddRadarScoringStrategies(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ScoringStrategySet>();
    }

    private static InvalidOperationException Rejects(IDictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        return Assert.Throws<InvalidOperationException>(
            () => services.AddRadarScoringStrategies(configuration));
    }

    [Fact]
    public void NoStrategiesConfigured_SynthesisesSingleDefaultPrimary_WithAmbientProfileWeights()
    {
        var set = Resolve(new Dictionary<string, string?>());

        var only = Assert.Single(set.Strategies);
        Assert.Equal("default", only.Name);
        Assert.True(only.IsPrimary);
        Assert.Same(only, set.Primary);
        // The ambient (blank) profile resolves to the code defaults — byte-identical to the pre-137 graph.
        Assert.Equal(new ScoringWeights(), only.Weights);
    }

    [Fact]
    public void NoStrategiesConfigured_StillHonoursAmbientScoringProfile()
    {
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Scoring:Profile"] = "low-media",
            ["Radar:Scoring:Profiles:low-media:MediaReachWeight"] = "0.02",
        });

        var only = Assert.Single(set.Strategies);
        Assert.Equal("default", only.Name);
        Assert.Equal("low-media", only.ScoringProfile);
        Assert.Equal(0.02, only.Weights.MediaReachWeight);
    }

    [Fact]
    public void TwoStrategies_ResolveTheirOwnProfiles_AndExactlyOnePrimary()
    {
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:Strategies:0:ScoringProfile"] = "default",
            ["Radar:Strategies:1:Name"] = "low-media",
            ["Radar:Strategies:1:ScoringProfile"] = "low-media",
            ["Radar:Scoring:Profiles:low-media:MediaReachWeight"] = "0.02",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        Assert.Equal(["baseline", "low-media"], set.Strategies.Select(s => s.Name).ToArray());
        Assert.Equal("baseline", set.Primary.Name);
        Assert.Equal(new ScoringWeights().MediaReachWeight, set.Strategies[0].Weights.MediaReachWeight);
        Assert.Equal(0.02, set.Strategies[1].Weights.MediaReachWeight);
        Assert.True(set.Strategies[0].IsPrimary);
        Assert.False(set.Strategies[1].IsPrimary);
    }

    [Fact]
    public void UnknownScoringProfile_FailsFast_NamingTheOffendingKey()
    {
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:Strategies:0:ScoringProfile"] = "does-not-exist",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        Assert.Contains("Radar:Strategies:0:ScoringProfile", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does-not-exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankStrategyName_FailsFast_NamingTheOffendingKey()
    {
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "   ",
            ["Radar:Strategies:0:ScoringProfile"] = "default",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        Assert.Contains("Radar:Strategies:0:Name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateStrategyName_FailsFast()
    {
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:Strategies:1:Name"] = "BASELINE",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        Assert.Contains("Radar:Strategies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrimaryStrategyNotInSet_FailsFast_NamingTheOffendingKey()
    {
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:PrimaryStrategy"] = "typo",
        });

        Assert.Contains("Radar:PrimaryStrategy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("typo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankPrimaryStrategy_WithConfiguredStrategies_FailsFast()
    {
        // Documented decision: which strategy owns the legacy storage location and the reported series is
        // load-bearing, so it is stated explicitly rather than silently defaulted to the first entry.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:Strategies:1:Name"] = "low-media",
        });

        Assert.Contains("Radar:PrimaryStrategy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrategyNameWithPathSeparator_FailsFast()
    {
        // A name is used verbatim as a storage directory segment, so a separator would escape the scores root.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "../escape",
            ["Radar:PrimaryStrategy"] = "../escape",
        });

        Assert.Contains("Radar:Strategies", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidWeightInStrategyProfile_FailsFastAtRegistration()
    {
        Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "broken",
            ["Radar:Strategies:0:ScoringProfile"] = "broken",
            ["Radar:Scoring:Profiles:broken:OpportunityAttentionDivisor"] = "0",
            ["Radar:PrimaryStrategy"] = "broken",
        });
    }
}
