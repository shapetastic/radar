using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Scoring;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 146 — binding <c>Radar:Strategies[i]:Formula</c> and <c>Radar:Strategies[i]:Channels</c>. The
/// layering rule is the point: <c>IConfiguration</c> never reaches <c>Radar.Application</c>, so the strings
/// and numbers are parsed in Infrastructure and only resolved types cross the boundary — while the
/// INVARIANTS live in <see cref="ScoringChannelSet"/> so they hold however a set is composed.
/// </summary>
public sealed class ScoringChannelBindingTests
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

    /// <summary>A well-formed three-channel budget: 0.50 patents + 0.30 insider + 0.20 breadth.</summary>
    private static Dictionary<string, string?> PatentsLed() => new()
    {
        ["Radar:Strategies:0:Name"] = "patents-led",
        ["Radar:Strategies:0:Formula"] = "radar-formula-v9",
        ["Radar:Strategies:0:Channels:0:Name"] = "patents",
        ["Radar:Strategies:0:Channels:0:Collectors:0"] = "patents",
        ["Radar:Strategies:0:Channels:0:Weight"] = "0.50",
        ["Radar:Strategies:0:Channels:0:Saturation"] = "3",
        ["Radar:Strategies:0:Channels:1:Name"] = "insider",
        ["Radar:Strategies:0:Channels:1:Collectors:0"] = "sec-form4",
        ["Radar:Strategies:0:Channels:1:Weight"] = "0.30",
        ["Radar:Strategies:0:Channels:1:Saturation"] = "2",
        ["Radar:Strategies:0:Channels:2:Name"] = "attention",
        ["Radar:Strategies:0:Channels:2:Kind"] = "breadth",
        ["Radar:Strategies:0:Channels:2:Weight"] = "0.20",
        ["Radar:Strategies:0:Channels:2:Saturation"] = "3",
        ["Radar:PrimaryStrategy"] = "patents-led",
    };

    [Fact]
    public void OmittedFormulaAndChannels_AreTheByteIdenticalDefault()
    {
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        var only = Assert.Single(set.Strategies);
        Assert.Equal(ScoreFormulaVersions.V8, only.Formula);
        Assert.True(only.Channels.IsEmpty);
        // The fold is a no-op, which is what keeps the pinned default fingerprints unmoved.
        Assert.Equal("rules=x;", only.Channels.Describe("rules=x;"));
    }

    [Fact]
    public void NoStrategiesConfigured_SynthesisedDefault_IsAlsoV8WithNoChannels()
    {
        var only = Assert.Single(Resolve(new Dictionary<string, string?>()).Strategies);

        Assert.Equal(ScoreFormulaVersions.V8, only.Formula);
        Assert.True(only.Channels.IsEmpty);
    }

    [Fact]
    public void AV9StrategyBindsItsWholeChannelBudget()
    {
        var only = Assert.Single(Resolve(PatentsLed()).Strategies);

        Assert.Equal(ScoreFormulaVersions.V9, only.Formula);
        // Canonicalised by name, so the declared order is irrelevant.
        Assert.Equal(["attention", "insider", "patents"], only.Channels.Channels.Select(c => c.Name));

        var patents = only.Channels.Channels.Single(c => c.Name == "patents");
        Assert.Equal(ScoringChannelKind.Collector, patents.Kind);
        Assert.Equal(["patents"], patents.Collectors);
        Assert.Equal(0.50, patents.Weight);
        Assert.Equal(3.0, patents.Saturation);

        var attention = only.Channels.Channels.Single(c => c.Name == "attention");
        Assert.Equal(ScoringChannelKind.Breadth, attention.Kind);
        Assert.Empty(attention.Collectors);
        Assert.Equal(0.20, attention.Weight);
    }

    [Fact]
    public void FormulaName_IsMatchedCaseInsensitively_AndCanonicalised()
    {
        var config = PatentsLed();
        config["Radar:Strategies:0:Formula"] = "  RADAR-Formula-V9 ";

        Assert.Equal(ScoreFormulaVersions.V9, Assert.Single(Resolve(config).Strategies).Formula);
    }

    [Fact]
    public void UnknownFormula_FailsFast_NamingTheConfigKeyAndTheKnownFormulas()
    {
        var config = PatentsLed();
        config["Radar:Strategies:0:Formula"] = "radar-formula-v99";

        var ex = Rejects(config);

        Assert.Contains("Radar:Strategies:0:Formula", ex.Message, StringComparison.Ordinal);
        Assert.Contains("radar-formula-v99", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScoreFormulaVersions.KnownList, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WeightsThatDoNotSumToOne_FailFast_NamingTheStrategyAndTheActualSum()
    {
        // The acceptance criterion, at the config boundary a real operator hits.
        var config = PatentsLed();
        config["Radar:Strategies:0:Channels:1:Weight"] = "0.20";  // 0.50 + 0.20 + 0.20 = 0.90

        var ex = Rejects(config);

        Assert.Contains("patents-led", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not 1.0", ex.Message, StringComparison.Ordinal);
        // The ACTUAL sum, verbatim — round-trip ("R") formatted, so what the operator reads is the exact
        // double that failed the check (0.5 + 0.2 + 0.2 is 0.8999999999999999, not 0.9) rather than a
        // prettified value that would not obviously explain a near-miss.
        Assert.Contains(
            (0.5 + 0.2 + 0.2).ToString("R", CultureInfo.InvariantCulture),
            ex.Message,
            StringComparison.Ordinal);
        // …and every declared channel's weight, so the offender is identifiable without re-reading the file.
        Assert.Contains("patents=0.5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WeightOutsideUnitRange_FailsFast()
    {
        var config = PatentsLed();
        config["Radar:Strategies:0:Channels:0:Weight"] = "1.50";
        config["Radar:Strategies:0:Channels:1:Weight"] = "-0.70";

        var ex = Rejects(config);

        Assert.Contains("patents-led", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[0, 1]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingWeightOrSaturation_FailsFast_RatherThanDefaulting()
    {
        var noWeight = PatentsLed();
        noWeight.Remove("Radar:Strategies:0:Channels:0:Weight");
        Assert.Contains("Weight is missing", Rejects(noWeight).Message, StringComparison.Ordinal);

        var noSaturation = PatentsLed();
        noSaturation.Remove("Radar:Strategies:0:Channels:2:Saturation");
        Assert.Contains("Saturation is missing", Rejects(noSaturation).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonNumericWeight_FailsFast()
    {
        var config = PatentsLed();
        config["Radar:Strategies:0:Channels:0:Weight"] = "half";

        Assert.Contains("is not a number", Rejects(config).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownChannelKind_FailsFast()
    {
        var config = PatentsLed();
        config["Radar:Strategies:0:Channels:2:Kind"] = "reach";

        var ex = Rejects(config);

        Assert.Contains("Kind", ex.Message, StringComparison.Ordinal);
        Assert.Contains("collector, breadth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarChannels_AreRejected_RatherThanReadAsNone()
    {
        // Same shape guard (and same reasoning) as SignalTypes: a scalar binds as a value with no children,
        // which would otherwise fall through to "no channels" and leave a v9 strategy scoring 0.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "oops",
            ["Radar:Strategies:0:Formula"] = "radar-formula-v9",
            ["Radar:Strategies:0:Channels"] = "patents",
            ["Radar:PrimaryStrategy"] = "oops",
        });

        Assert.Contains("Radar:Strategies:0:Channels", ex.Message, StringComparison.Ordinal);
        Assert.Contains("JSON ARRAY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarCollectors_AreRejected()
    {
        var config = PatentsLed();
        config.Remove("Radar:Strategies:0:Channels:0:Collectors:0");
        config["Radar:Strategies:0:Channels:0:Collectors"] = "patents";

        var ex = Rejects(config);

        Assert.Contains("Collectors", ex.Message, StringComparison.Ordinal);
        Assert.Contains("JSON ARRAY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectorChannelWithNoCollectors_FailsFast()
    {
        var config = PatentsLed();
        config.Remove("Radar:Strategies:0:Channels:0:Collectors:0");

        Assert.Contains("declares no collectors", Rejects(config).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BreadthChannelDeclaringCollectors_FailsFast()
    {
        var config = PatentsLed();
        config["Radar:Strategies:0:Channels:2:Collectors:0"] = "newssearch";

        Assert.Contains("cross-source", Rejects(config).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankChannelName_FailsFast_NamingTheConfigKey()
    {
        var config = PatentsLed();
        config["Radar:Strategies:0:Channels:1:Name"] = "  ";

        var ex = Rejects(config);

        Assert.Contains("Radar:Strategies:0:Channels:1:Name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelsOnAV8Strategy_FailFast()
    {
        var config = PatentsLed();
        config.Remove("Radar:Strategies:0:Formula");

        Assert.Contains("silently ignored", Rejects(config).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AV8StrategyAndAV9Strategy_CoexistOverOneCollectionPass()
    {
        // The whole point of the slice: an experiment you can run ALONGSIDE the existing series.
        var config = PatentsLed();
        config["Radar:Strategies:1:Name"] = "baseline";
        config["Radar:PrimaryStrategy"] = "baseline";

        var set = Resolve(config);

        Assert.Equal(2, set.Strategies.Count);
        Assert.Equal(ScoreFormulaVersions.V9, set.Strategies[0].Formula);
        Assert.Equal(ScoreFormulaVersions.V8, set.Strategies[1].Formula);
        Assert.True(set.Strategies[1].IsPrimary);
        Assert.True(set.Strategies[1].Channels.IsEmpty);
    }
}
