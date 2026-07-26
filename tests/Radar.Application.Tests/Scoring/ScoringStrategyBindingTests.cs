using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Scoring;
using Radar.Domain.Signals;
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

    // ---- Spec 138: the per-strategy signal-type set ----------------------------------------------------

    [Fact]
    public void NoSignalTypes_DefaultsToAllTypes()
    {
        // The byte-identical default at the binding seam: an entry that says nothing about SignalTypes gets
        // the canonical "consume everything" filter, which hashes as a no-op.
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        Assert.Same(SignalTypeFilter.All, Assert.Single(set.Strategies).SignalTypes);
    }

    [Fact]
    public void SynthesisedDefaultStrategy_ConsumesAllTypes()
    {
        var set = Resolve(new Dictionary<string, string?>());

        Assert.Same(SignalTypeFilter.All, Assert.Single(set.Strategies).SignalTypes);
    }

    [Fact]
    public void SignalTypes_BindToTheDeclaredEnumMembers()
    {
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "insider-only",
            ["Radar:Strategies:0:SignalTypes:0"] = "InsiderBuying",
            ["Radar:Strategies:1:Name"] = "filings",
            ["Radar:Strategies:1:SignalTypes:0"] = "GuidanceChange",
            ["Radar:Strategies:1:SignalTypes:1"] = "GovernmentContract",
            ["Radar:PrimaryStrategy"] = "insider-only",
        });

        Assert.Equal([SignalType.InsiderBuying], set.Strategies[0].SignalTypes.Types);
        Assert.False(set.Strategies[0].SignalTypes.IsAll);
        // Ordered by underlying enum value, NOT by config list order.
        Assert.Equal(
            [SignalType.GuidanceChange, SignalType.GovernmentContract],
            set.Strategies[1].SignalTypes.Types);
    }

    [Fact]
    public void SignalTypes_AreMatchedCaseInsensitivelyByName()
    {
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "insider-only",
            ["Radar:Strategies:0:SignalTypes:0"] = "insiderbuying",
            ["Radar:PrimaryStrategy"] = "insider-only",
        });

        Assert.Equal([SignalType.InsiderBuying], Assert.Single(set.Strategies).SignalTypes.Types);
    }

    [Fact]
    public void SignalTypes_ListingEveryMember_CanonicalisesToAll()
    {
        // Spelling out the full set must be byte-identical to omitting the key, or the default fingerprint
        // would fork for a config that changed nothing.
        var config = new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "everything",
            ["Radar:PrimaryStrategy"] = "everything",
        };

        var names = Enum.GetNames<SignalType>();
        for (var i = 0; i < names.Length; i++)
        {
            config[$"Radar:Strategies:0:SignalTypes:{i}"] = names[i];
        }

        var set = Resolve(config);

        Assert.Same(SignalTypeFilter.All, Assert.Single(set.Strategies).SignalTypes);
    }

    [Fact]
    public void UnknownSignalType_FailsFast_NamingTheKeyAndTheValue()
    {
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "typo",
            ["Radar:Strategies:0:SignalTypes:0"] = "InsiderTransaction",
            ["Radar:PrimaryStrategy"] = "typo",
        });

        Assert.Contains("Radar:Strategies:0:SignalTypes:0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("InsiderTransaction", ex.Message, StringComparison.Ordinal);
        // The message lists the real members so the fix is obvious from the log line alone.
        Assert.Contains(nameof(SignalType.InsiderBuying), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericSignalType_IsRejected()
    {
        // Enum.TryParse would happily accept "5" (and any other number, declared or not); matching against
        // the declared NAMES is what makes this a startup failure instead of a strategy that scores nothing.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "numeric",
            ["Radar:Strategies:0:SignalTypes:0"] = "5",
            ["Radar:PrimaryStrategy"] = "numeric",
        });

        Assert.Contains("Radar:Strategies:0:SignalTypes:0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'5'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeclaredNumericSignalType_IsRejected()
    {
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "numeric",
            ["Radar:Strategies:0:SignalTypes:0"] = "9999",
            ["Radar:PrimaryStrategy"] = "numeric",
        });

        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySignalTypesArray_ResolvesToAllTypes_AndDoesNotTripTheScalarGuard()
    {
        // The spec's "omitted OR EMPTY ⇒ all signal types", pinned at the BINDING seam — SignalTypeFilter's
        // own Create_Empty_IsAll never runs through a config provider, which is exactly why the scalar guard
        // was briefly able to reject "SignalTypes": [] unnoticed. An empty JSON array binds to the empty
        // STRING (not null), so "" here is the faithful stand-in for [] and must resolve to All, not throw.
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "empty-array",
            ["Radar:Strategies:0:SignalTypes"] = "",
            ["Radar:PrimaryStrategy"] = "empty-array",
        });

        Assert.Same(SignalTypeFilter.All, Assert.Single(set.Strategies).SignalTypes);
    }

    [Fact]
    public void ScalarSignalTypes_IsRejected_RatherThanSilentlyMeaningAllTypes()
    {
        // "SignalTypes": "InsiderBuying" (the array brackets forgotten) binds as a VALUE with no children, so
        // without this guard it would fall through to "all types" — stamping and scoring BROAD a strategy the
        // operator wrote to be narrow, which is exactly the failure this slice exists to prevent.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "scalar",
            ["Radar:Strategies:0:SignalTypes"] = "InsiderBuying",
            ["Radar:PrimaryStrategy"] = "scalar",
        });

        Assert.Contains("Radar:Strategies:0:SignalTypes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("InsiderBuying", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ARRAY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlankSignalTypeEntry_IsRejected()
    {
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "blank",
            ["Radar:Strategies:0:SignalTypes:0"] = "   ",
            ["Radar:PrimaryStrategy"] = "blank",
        });

        Assert.Contains("Radar:Strategies:0:SignalTypes:0", ex.Message, StringComparison.Ordinal);
    }
}
