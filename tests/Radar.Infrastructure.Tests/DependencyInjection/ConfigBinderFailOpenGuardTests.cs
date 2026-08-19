using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// Spec 174 — the four pre-149 config binders (named scoring profiles, insider tiers, media collapse,
/// attention tiers) must fail FAST on a typo'd key, a scalar-where-object section, or an
/// existing-but-unbindable section, instead of silently binding the code defaults while the run reads as
/// tuned (the 2026-08-19 arch-sweep M-1 fail-open). Every well-formed / absent / empty configuration must
/// keep resolving byte-identically — the guards reject or accept; they never change what a valid config
/// resolves to.
/// </summary>
public sealed class ConfigBinderFailOpenGuardTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static T Resolve<T>(Action<IServiceCollection> register)
        where T : class
    {
        var services = new ServiceCollection();
        register(services);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<T>();
    }

    // ---------------------------------------------------------------------------------------------------
    // Site 1 — ResolveScoringProfile (Radar:Scoring:Profiles:{name}), both callers
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ScoringProfile_TypodWeightKey_FailsFast_NamingPathKeyAndSortedValidNames()
    {
        var configuration = Config(
            ("Radar:Scoring:Profile", "exp"),
            ("Radar:Scoring:Profiles:exp:MediaReachWieght", "0.1"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringWeights(configuration));

        Assert.Contains("Radar:Scoring:Profiles:exp:MediaReachWieght", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'MediaReachWieght'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Radar:Scoring:Profile", ex.Message, StringComparison.Ordinal);
        // The sorted valid-name list is rendered so the operator can see the near-miss.
        Assert.Contains("MediaReachWeight", ex.Message, StringComparison.Ordinal);
        Assert.Contains("valid names:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoringProfile_ScalarBody_FailsFast()
    {
        var configuration = Config(
            ("Radar:Scoring:Profile", "exp"),
            ("Radar:Scoring:Profiles:exp", "0.1"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringWeights(configuration));

        Assert.Contains("Radar:Scoring:Profiles:exp is the scalar '0.1'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("JSON OBJECT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoringProfile_KnownKeyCarryingNestedObject_FailsFast()
    {
        var configuration = Config(
            ("Radar:Scoring:Profile", "exp"),
            ("Radar:Scoring:Profiles:exp:MediaReachWeight:Value", "0.1"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringWeights(configuration));

        Assert.Contains("carries no numeric value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'exp'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoringProfile_KnownKeyExplicitNull_FailsFast()
    {
        var configuration = Config(
            ("Radar:Scoring:Profile", "exp"),
            ("Radar:Scoring:Profiles:exp:MediaReachWeight", null));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringWeights(configuration));

        Assert.Contains("carries no numeric value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoringProfile_NonNumericValue_Rethrown_NamingProfileAndRequestingKey_WithInnerException()
    {
        var configuration = Config(
            ("Radar:Scoring:Profile", "exp"),
            ("Radar:Scoring:Profiles:exp:MediaReachWeight", "not-a-number"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringWeights(configuration));

        Assert.Contains("'exp'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Radar:Scoring:Profile", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void ScoringProfile_StrategyCaller_GetsTheSameGuards_NamingTheStrategysRequestingKey()
    {
        // The guards live in the ONE shared ResolveScoringProfile implementation, so the per-strategy
        // ScoringProfile caller gets them for free — and its failures name the strategy's own config key.
        var configuration = Config(
            ("Radar:PrimaryStrategy", "a"),
            ("Radar:Strategies:0:Name", "a"),
            ("Radar:Strategies:0:ScoringProfile", "exp"),
            ("Radar:Scoring:Profiles:exp:MediaReachWieght", "0.1"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarScoringStrategies(configuration));

        Assert.Contains("Radar:Strategies:0:ScoringProfile", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'MediaReachWieght'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoringProfile_WellFormed_ResolvesByteIdentically()
    {
        var weights = Resolve<ScoringWeights>(s => s.AddRadarScoringWeights(Config(
            ("Radar:Scoring:Profile", "exp"),
            ("Radar:Scoring:Profiles:exp:MediaReachWeight", "0.05"))));

        Assert.Equal(new ScoringWeights { MediaReachWeight = 0.05 }, weights);
    }

    [Fact]
    public void ScoringProfile_ExplicitlyNullSection_StillBindsCodeDefaults()
    {
        // The one legitimate quiet case: a profile section that exists with no children and no scalar value
        // (an explicitly-null/empty object) is an honest "all defaults", not a mis-shape.
        var weights = Resolve<ScoringWeights>(s => s.AddRadarScoringWeights(Config(
            ("Radar:Scoring:Profile", "exp"),
            ("Radar:Scoring:Profiles:exp", ""))));

        Assert.Equal(new ScoringWeights(), weights);
    }

    // ---------------------------------------------------------------------------------------------------
    // Site 2 — AddRadarInsiderMateriality (Radar:Insider:Profiles:{name})
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Insider_TypodTopLevelKey_FailsFast_NamingKeyAndSortedValidNames()
    {
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:ClusterBost", "2"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains("Radar:Insider:Profiles:exp:ClusterBost", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'ClusterBost'", ex.Message, StringComparison.Ordinal);
        // Sorted valid names, reflection-derived from InsiderMaterialityWeights.
        Assert.Contains("BuyTiers, ClusterBoost, SellTiers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insider_ScalarProfileBody_FailsFast()
    {
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp", "1"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains("Radar:Insider:Profiles:exp is the scalar '1'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insider_ScalarBuyTiers_FailsFast_NamingTheTable()
    {
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:BuyTiers", "big"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains("Radar:Insider:Profiles:exp:BuyTiers is the scalar 'big'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("JSON ARRAY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insider_UnknownTierEntryKey_FailsFast()
    {
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:BuyTiers:0:MinInclusive", "250000"),
            ("Radar:Insider:Profiles:exp:BuyTiers:0:Strenght", "6"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains("'Strenght'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MinInclusive, Strength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insider_ScalarTierEntry_FailsFast_NamingTheEntryPath()
    {
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:BuyTiers:0", "250000"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains("Radar:Insider:Profiles:exp:BuyTiers:0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MinInclusive, Strength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insider_NonNumericMinInclusive_FailsFast_NamingTheEntryPath()
    {
        // The existing-but-unbindable table, and the WORST of the four sites: the binder does not even
        // throw here — BindCollection swallows a failed conversion inside a list element and silently DROPS
        // the tier (measured: this config used to bind an EMPTY BuyTiers table). So the guard must reject
        // the value explicitly, before the bind.
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:BuyTiers:0:MinInclusive", "lots"),
            ("Radar:Insider:Profiles:exp:BuyTiers:0:Strength", "6"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains(
            "Radar:Insider:Profiles:exp:BuyTiers:0:MinInclusive is 'lots'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'exp'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a number", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insider_NonNumericClusterBoost_FailsFast_NamingTheProfile_WithInnerException()
    {
        // Verifies what GetValue actually does on a present-but-non-numeric scalar: it THROWS (the binder
        // wraps the conversion failure), and the wrapper here names the profile with the binder exception
        // preserved as InnerException.
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:ClusterBoost", "not-a-number"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains("Radar:Insider:Profiles:exp:ClusterBoost", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'exp'", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Insider_NestedObjectClusterBoost_FailsFast()
    {
        // GetValue SILENTLY returns the default when the key carries children (its Value is null) — the one
        // GetValue shape that does not throw on its own, so it is guarded explicitly.
        var configuration = Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:ClusterBoost:Value", "2"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarInsiderMateriality(configuration));

        Assert.Contains("Radar:Insider:Profiles:exp:ClusterBoost", ex.Message, StringComparison.Ordinal);
        Assert.Contains("carries no numeric value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insider_AbsentTable_StillFallsBackToCodeDefault()
    {
        var weights = Resolve<InsiderMaterialityWeights>(s => s.AddRadarInsiderMateriality(Config(
            ("Radar:Insider:Profile", "exp"),
            ("Radar:Insider:Profiles:exp:ClusterBoost", "2"))));

        var defaults = new InsiderMaterialityWeights();
        Assert.Equal(2, weights.ClusterBoost);
        Assert.Equal((defaults with { ClusterBoost = 2 }).CanonicalDescriptor(), weights.CanonicalDescriptor());
    }

    // ---------------------------------------------------------------------------------------------------
    // Site 3 — AddRadarMediaCollapse (Radar:Scoring:MediaCollapse)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void MediaCollapse_ScalarSection_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarMediaCollapse(Config(
                ("Radar:Scoring:MediaCollapse", "3"))));

        Assert.Contains("Radar:Scoring:MediaCollapse is the scalar '3'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaCollapse_TypodKey_FailsFast_NamingSortedValidNames()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarMediaCollapse(Config(
                ("Radar:Scoring:MediaCollapse:EventWindowDay", "5"))));

        Assert.Contains("'EventWindowDay'", ex.Message, StringComparison.Ordinal);
        // The reflection-derived set is exactly { EventWindowDays } — the derived get-only EventWindow
        // property must NOT read as a bindable key (the binder cannot set it either).
        Assert.Contains("valid names: EventWindowDays)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaCollapse_NonNumericValue_Rethrown_NamingTheSection_WithInnerException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarMediaCollapse(Config(
                ("Radar:Scoring:MediaCollapse:EventWindowDays", "not-a-number"))));

        Assert.Contains("Radar:Scoring:MediaCollapse", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void MediaCollapse_NestedObjectValue_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarMediaCollapse(Config(
                ("Radar:Scoring:MediaCollapse:EventWindowDays:Value", "5"))));

        Assert.Contains("carries no numeric value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaCollapse_AbsentAndWellFormed_ResolveByteIdentically()
    {
        var absent = Resolve<MediaCollapseOptions>(s => s.AddRadarMediaCollapse(Config()));
        Assert.Equal(new MediaCollapseOptions(), absent);

        var tuned = Resolve<MediaCollapseOptions>(s => s.AddRadarMediaCollapse(Config(
            ("Radar:Scoring:MediaCollapse:EventWindowDays", "5"))));
        Assert.Equal(5.0, tuned.EventWindowDays);
    }

    // ---------------------------------------------------------------------------------------------------
    // Site 4 — AddRadarAttentionTiers (Radar:Attention)
    // ---------------------------------------------------------------------------------------------------

    private static AttentionSourceTierOptions ResolveAttention(params (string Key, string? Value)[] pairs) =>
        Resolve<AttentionSourceTierOptions>(s => s.AddRadarAttentionTiers(Config(pairs)));

    [Fact]
    public void Attention_AbsentSection_StillYieldsTheCuratedDefault()
    {
        Assert.Same(AttentionSourceTierOptions.Default, ResolveAttention());
    }

    [Fact]
    public void Attention_ScalarSection_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(("Radar:Attention", "0.25"))));

        Assert.Contains("Radar:Attention is the scalar '0.25'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_UnknownTopLevelKey_FailsFast_NamingSortedValidNames()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:UnknownWieght", "0.25"))));

        Assert.Contains("'UnknownWieght'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("valid names: SourceTiers, UnknownWeight)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_StaticDefaultProperty_IsNotABindableKey()
    {
        // BindingFlags.Instance excludes the static Default property from the reflection-derived set, so an
        // operator writing "Default" as a config key gets the fail-fast rather than a silent no-op.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:Default:UnknownWeight", "0.25"))));

        Assert.Contains("'Default'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_FreeFormTierNames_Accepted()
    {
        var options = ResolveAttention(
            ("Radar:Attention:UnknownWeight", "0.3"),
            ("Radar:Attention:SourceTiers:MyNicheTier:Weight", "0.5"),
            ("Radar:Attention:SourceTiers:MyNicheTier:Publishers:0", "Some Outlet"));

        Assert.Equal(0.3, options.UnknownWeight);
        var tier = options.SourceTiers["MyNicheTier"];
        Assert.Equal(0.5, tier.Weight);
        Assert.Equal(["Some Outlet"], tier.Publishers);
    }

    [Fact]
    public void Attention_UnknownKeyInsideTierValue_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:SourceTiers:Mill:Wieght", "0.1"))));

        Assert.Contains("'Wieght'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("valid names: Publishers, Weight)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_ScalarTierValue_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:SourceTiers:Mill", "0.1"))));

        Assert.Contains("Radar:Attention:SourceTiers:Mill", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scalar '0.1'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_ScalarPublishers_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:SourceTiers:Mill:Weight", "0.1"),
                ("Radar:Attention:SourceTiers:Mill:Publishers", "MarketBeat"))));

        Assert.Contains("Radar:Attention:SourceTiers:Mill:Publishers", ex.Message, StringComparison.Ordinal);
        Assert.Contains("JSON ARRAY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_NonNumericTierWeight_FailsFast_NamingTheTierPath()
    {
        // Inside the SourceTiers DICTIONARY the binder swallows a failed element conversion (measured: this
        // config used to bind without a throw, silently losing the tier), so the guard rejects the value
        // explicitly, before the bind.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:SourceTiers:Mill:Weight", "not-a-number"))));

        Assert.Contains(
            "Radar:Attention:SourceTiers:Mill:Weight is 'not-a-number'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a number", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_NonNumericUnknownWeight_Rethrown_NamingTheSection_WithInnerException()
    {
        // At the top level (a plain property, not a collection element) the binder DOES throw — rethrown
        // naming Radar:Attention with the binder exception preserved as InnerException.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:UnknownWeight", "not-a-number"))));

        Assert.Contains("Radar:Attention", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Attention_NestedObjectPublisherEntry_FailsFast()
    {
        // A nested object where a publisher NAME was meant would be silently dropped by the binder,
        // quietly shortening the list — rejected explicitly.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:SourceTiers:Mill:Weight", "0.1"),
                ("Radar:Attention:SourceTiers:Mill:Publishers:0:Name", "MarketBeat"))));

        Assert.Contains(
            "Radar:Attention:SourceTiers:Mill:Publishers:0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("publisher name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_NestedObjectUnknownWeight_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddRadarAttentionTiers(Config(
                ("Radar:Attention:UnknownWeight:Value", "0.25"))));

        Assert.Contains("Radar:Attention:UnknownWeight", ex.Message, StringComparison.Ordinal);
        Assert.Contains("carries no numeric value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_WellFormedSection_BindsIdenticallyToTheRawBinder()
    {
        // The guards accept-or-reject; they never change what a valid section resolves to. Compare the
        // guarded resolution against the raw pre-174 bind of the same configuration.
        var configuration = Config(
            ("Radar:Attention:UnknownWeight", "0.4"),
            ("Radar:Attention:SourceTiers:Genuine:Weight", "1.0"),
            ("Radar:Attention:SourceTiers:Genuine:Publishers:0", "Reuters"),
            ("Radar:Attention:SourceTiers:Genuine:Publishers:1", "Bloomberg"));

        var guarded = Resolve<AttentionSourceTierOptions>(s => s.AddRadarAttentionTiers(configuration));
        var raw = configuration.GetSection("Radar:Attention").Get<AttentionSourceTierOptions>();

        Assert.NotNull(raw);
        Assert.Equal(raw.UnknownWeight, guarded.UnknownWeight);
        Assert.Equal(raw.SourceTiers.Keys.Order(StringComparer.Ordinal), guarded.SourceTiers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(raw.SourceTiers["Genuine"].Weight, guarded.SourceTiers["Genuine"].Weight);
        Assert.Equal(raw.SourceTiers["Genuine"].Publishers, guarded.SourceTiers["Genuine"].Publishers);
    }
}
