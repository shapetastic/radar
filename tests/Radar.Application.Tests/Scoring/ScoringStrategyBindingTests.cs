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

    // ---- Spec 149: inline per-strategy weight overrides -------------------------------------------------

    [Fact]
    public void NoInlineWeights_IsByteIdenticalToTheProfilesWeights()
    {
        // The byte-identical default at the binding seam: an entry that says nothing about Weights gets
        // exactly what its ScoringProfile resolved to — the SAME instance, so there is not even a copy that
        // could differ.
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        Assert.Equal(new ScoringWeights(), Assert.Single(set.Strategies).Weights);
    }

    [Fact]
    public void InlineWeights_MergeOrderIsDefaultsThenProfileThenInline_LastWins()
    {
        // THE documented merge order, pinned end to end in one config:
        //   * RecencyFloor        — touched by neither ⇒ the CODE DEFAULT survives;
        //   * MediaReachWeight    — set by the profile only ⇒ the PROFILE value survives;
        //   * VelocitySteady      — set by BOTH ⇒ the INLINE value wins (last wins);
        //   * DiversityTarget     — set inline only ⇒ the INLINE value applies over the code default.
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "tuned",
            ["Radar:Strategies:0:ScoringProfile"] = "experiment",
            ["Radar:Strategies:0:Weights:VelocitySteady"] = "40",
            ["Radar:Strategies:0:Weights:DiversityTarget"] = "5",
            ["Radar:Scoring:Profiles:experiment:MediaReachWeight"] = "0.02",
            ["Radar:Scoring:Profiles:experiment:VelocitySteady"] = "60",
            ["Radar:PrimaryStrategy"] = "tuned",
        });

        var weights = Assert.Single(set.Strategies).Weights;

        Assert.Equal(new ScoringWeights().RecencyFloor, weights.RecencyFloor);
        Assert.Equal(0.02, weights.MediaReachWeight);
        Assert.Equal(40.0, weights.VelocitySteady);
        Assert.Equal(5.0, weights.DiversityTarget);
        // The strategy still records the profile it resolved FROM — the override is a delta on top of it, not
        // a replacement for it, and the resolved values themselves are what the fingerprint hashes.
        Assert.Equal("experiment", set.Strategies[0].ScoringProfile);
    }

    [Fact]
    public void InlineWeights_ApplyOverTheCodeDefaults_WhenNoProfileIsNamed()
    {
        // The headline spec-149 use case: turn the notedness discount off for ONE strategy without inventing
        // a whole profile for it.
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "attention-light",
            ["Radar:Strategies:0:Weights:FollowingTierDiscountWeight"] = "0.0",
            ["Radar:Strategies:0:Weights:OpportunityAttentionDiscountWeight"] = "0.25",
            ["Radar:PrimaryStrategy"] = "attention-light",
        });

        var weights = Assert.Single(set.Strategies).Weights;

        Assert.Equal(0.0, weights.FollowingTierDiscountWeight);
        Assert.Equal(0.25, weights.OpportunityAttentionDiscountWeight);
        // Everything else is untouched: an override sets exactly the field it names.
        Assert.Equal(new ScoringWeights() with
        {
            FollowingTierDiscountWeight = 0.0,
            OpportunityAttentionDiscountWeight = 0.25,
        }, weights);
    }

    [Fact]
    public void InlineWeights_AffectOnlyTheStrategyThatDeclaredThem()
    {
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "baseline",
            ["Radar:Strategies:1:Name"] = "attention-light",
            ["Radar:Strategies:1:Weights:FollowingTierDiscountWeight"] = "0.0",
            ["Radar:PrimaryStrategy"] = "baseline",
        });

        Assert.Equal(new ScoringWeights(), set.Strategies[0].Weights);
        Assert.Equal(0.0, set.Strategies[1].Weights.FollowingTierDiscountWeight);
    }

    [Fact]
    public void UnknownInlineWeightKey_FailsFast_NamingTheStrategyAndTheKey()
    {
        // THE fail-open this closes. ConfigurationBinder silently ignores a key that matches no property, so
        // without this guard a typo'd override would leave the ambient value in place and produce a strategy
        // that is stamped, scored and RANKED as tuned while being nothing of the sort — the exact shape spec
        // 138 already had to close once.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "attention-light",
            ["Radar:Strategies:0:Weights:FollowingTierDiscountWieght"] = "0.0",
            ["Radar:PrimaryStrategy"] = "attention-light",
        });

        Assert.Contains("attention-light", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FollowingTierDiscountWieght", ex.Message, StringComparison.Ordinal);
        // The message lists the real field names so the fix is obvious from the log line alone.
        Assert.Contains(
            nameof(ScoringWeights.FollowingTierDiscountWeight), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineWeightKeys_AreMatchedCaseInsensitively_LikeTheBinderItself()
    {
        // DELIBERATE, and the reason is that the validator must answer exactly the question the binder
        // answers. ConfigurationBinder matches config keys to properties case-insensitively, so a
        // case-SENSITIVE validator would reject a key that binds perfectly well — and, worse, its notion of
        // "unknown" would stop being the binder's. A genuine near-miss is unknown to both and still fails.
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "lowercase",
            ["Radar:Strategies:0:Weights:followingtierdiscountweight"] = "0.0",
            ["Radar:PrimaryStrategy"] = "lowercase",
        });

        Assert.Equal(0.0, Assert.Single(set.Strategies).Weights.FollowingTierDiscountWeight);
    }

    [Fact]
    public void OutOfRangeInlineWeight_FailsFast_NamingTheStrategy()
    {
        // ScoringWeights.Validate runs on the MERGED result, so an inline override cannot smuggle past a
        // check its profile would have failed.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "broken",
            ["Radar:Strategies:0:Weights:OpportunityAttentionDivisor"] = "0",
            ["Radar:PrimaryStrategy"] = "broken",
        });

        Assert.Contains("broken", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            nameof(ScoringWeights.OpportunityAttentionDivisor), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineWeightsBreakingACrossFieldInvariant_FailFast()
    {
        // A value that is fine on its own but breaks an invariant SPANNING fields — here the monotone
        // Mega >= Large >= Mid >= Small tier ordering — is exactly why Validate has to run on the merged
        // result rather than on the inline block alone.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "non-monotone",
            ["Radar:Strategies:0:Weights:FollowingTierDiscountMega"] = "0.05",
            ["Radar:PrimaryStrategy"] = "non-monotone",
        });

        Assert.Contains("non-monotone", ex.Message, StringComparison.Ordinal);
        Assert.Contains("monotone", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void NonNumericInlineWeightValue_FailsFast_NamingTheStrategy_RatherThanBindingToZero(string value)
    {
        // ConfigurationBinder's own message names the indexed PATH but never the strategy, so a bind failure
        // used to be the ONE inline-Weights failure that did not say which of several near-identical
        // strategies was broken. The rethrow names it and keeps the binder's exception as InnerException, so
        // the offending key, the target type and the underlying conversion error all survive.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "text",
            ["Radar:Strategies:0:Weights:RecencyFloor"] = value,
            ["Radar:PrimaryStrategy"] = "text",
        });

        Assert.Contains("'text'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Radar:Strategies:0:Weights", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ScoringWeights.RecencyFloor), ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void ScalarWeights_IsRejected_RatherThanSilentlyMeaningNoOverrides()
    {
        // "Weights": "0.25" (the object forgotten) binds as a VALUE with no children, so without this guard it
        // would fall through to "no overrides" — silently scoring an UNTUNED strategy the operator wrote to be
        // tuned. Mirrors the SignalTypes / Channels shape guards.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "scalar",
            ["Radar:Strategies:0:Weights"] = "0.25",
            ["Radar:PrimaryStrategy"] = "scalar",
        });

        Assert.Contains("Radar:Strategies:0:Weights", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.25", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OBJECT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObjectValuedInlineWeight_IsRejected_RatherThanBeingSilentlyIgnored()
    {
        // The per-ENTRY shape guard: every ScoringWeights field is a plain number, so "RecencyFloor": { ... }
        // can never be a valid override. The key is KNOWN, so the unknown-key guard does not catch it, and
        // binding a childful section onto a double leaves the profile value in place — an untuned strategy
        // that reads as tuned, which is the same fail-open the unknown-key guard exists to close.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "object-valued",
            ["Radar:Strategies:0:Weights:RecencyFloor:Value"] = "0.0",
            ["Radar:PrimaryStrategy"] = "object-valued",
        });

        Assert.Contains("Radar:Strategies:0:Weights:RecencyFloor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("object-valued", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NUMBER", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullInlineWeightValue_IsRejected_RatherThanSilentlyDisablingADiscount()
    {
        // "FollowingTierDiscountWeight": null carries no number either. Rejecting it is what stops an
        // operator's explicit null from resolving to some value they never wrote — here, a discount weight of
        // 0, i.e. a silently DISABLED notedness discount (spec 149) on a strategy that looks tuned.
        var ex = Rejects(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "null-valued",
            ["Radar:Strategies:0:Weights:FollowingTierDiscountWeight"] = null,
            ["Radar:PrimaryStrategy"] = "null-valued",
        });

        Assert.Contains(
            "Radar:Strategies:0:Weights:FollowingTierDiscountWeight", ex.Message, StringComparison.Ordinal);
        Assert.Contains("null-valued", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NUMBER", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyWeightsObject_IsAccepted_AndChangesNothing()
    {
        // An empty JSON object binds to the empty STRING, indistinguishable from "Weights": "" — the same
        // representation problem the spec-138 SignalTypes guard documents. Blank must therefore mean "no
        // overrides", which is also what the scalar guard's message tells the operator to write.
        var set = Resolve(new Dictionary<string, string?>
        {
            ["Radar:Strategies:0:Name"] = "empty-object",
            ["Radar:Strategies:0:Weights"] = "",
            ["Radar:PrimaryStrategy"] = "empty-object",
        });

        Assert.Equal(new ScoringWeights(), Assert.Single(set.Strategies).Weights);
    }

    [Fact]
    public void InlineWeights_AreIgnoredForTheSynthesisedDefaultStrategy_BecauseThereIsNoEntryToDeclareThemOn()
    {
        // Recorded so the absence is a decision rather than an oversight: inline Weights live ON a
        // Radar:Strategies entry. With no entries at all, composition synthesises the single "default"
        // strategy from the ambient Radar:Scoring:Profile — unchanged, which is what keeps every existing
        // config byte-identical.
        var set = Resolve(new Dictionary<string, string?>());

        Assert.Equal(new ScoringWeights(), Assert.Single(set.Strategies).Weights);
    }
}
