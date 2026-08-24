using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;
using Radar.TestSupport;

namespace Radar.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// Spec 174's load-bearing compatibility gate: the new config-binder guards must reject TYPOS, never the
/// profiles Radar actually runs. Every shipped <c>scripts/run-profiles/*.json</c> overlay, composed onto
/// <c>default.json</c> exactly the way <c>run-radar.ps1</c> composes them, must pass every guarded binder —
/// and the guarded sites must resolve byte-identically to their pre-174 values.
/// <para>
/// The composition is mirrored faithfully rather than approximated, through the SHARED
/// <see cref="RunProfileMirror"/> (spec 187 §6 — one mirror, two consumers, no second copy to drift): the
/// script flattens each profile's JSON into <c>Radar:A:B:C</c> keys (arrays become <c>…:0</c>, <c>…:1</c>,
/// …), SKIPS every <c>_comment*</c> annotation key at any depth, and merges the overlay into the base ONE
/// FLAT KEY AT A TIME (<c>$merged[$k] = $overlay[$k]</c>, overlay wins), then feeds the merged dictionary to
/// the Worker as <c>--Radar:…=value</c> command-line args. The script's remaining additions
/// (output-directory overrides, the SEC User-Agent, <c>Radar:RunMode</c>) touch none of the guarded
/// sections, so they are not replayed here.
/// </para>
/// </summary>
public sealed class RunProfileGuardCompatibilityTests
{
    /// <summary>
    /// Composes <c>default.json</c> plus an optional overlay through the SHARED
    /// <see cref="RunProfileMirror"/> — the one C# mirror of <c>run-radar.ps1</c>'s flatten-and-merge, so
    /// this test and the Worker-side full-configuration guard test cannot drift from each other or from the
    /// script (spec 187 §6).
    /// </summary>
    private static IConfiguration Compose(string? overlayProfileName) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(RunProfileMirror.Compose(overlayProfileName))
            .Build();

    /// <summary>Runs every spec-174-guarded binder over the composed configuration.</summary>
    private static ServiceProvider BindAllGuardedSites(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddRadarScoringWeights(configuration);
        services.AddRadarScoringStrategies(configuration);
        services.AddRadarInsiderMateriality(configuration);
        services.AddRadarMediaCollapse(configuration);
        services.AddRadarAttentionTiers(configuration);
        return services.BuildServiceProvider();
    }

    public static TheoryData<string> ShippedOverlays()
    {
        var data = new TheoryData<string>();
        foreach (var profileName in RunProfileMirror.OverlayProfileNames())
        {
            data.Add(profileName);
        }

        return data;
    }

    /// <summary>
    /// Every composition a live run can produce: the baseline alone (<c>null</c>) and the baseline with
    /// each shipped overlay on top.
    /// </summary>
    public static TheoryData<string?> ComposableProfiles()
    {
        var data = new TheoryData<string?> { null };
        foreach (var profileName in RunProfileMirror.OverlayProfileNames())
        {
            data.Add(profileName);
        }

        return data;
    }

    [Fact]
    public void DefaultProfile_PassesEveryGuard_AndResolvesTheGuardedSitesByteIdentically()
    {
        using var provider = BindAllGuardedSites(Compose(overlayProfileName: null));

        // default.json deliberately omits Radar:Scoring / Radar:Insider / Radar:Scoring:MediaCollapse /
        // Radar:Attention, so every guarded site must keep resolving the exact code defaults.
        Assert.Equal(new ScoringWeights(), provider.GetRequiredService<ScoringWeights>());
        Assert.Equal(
            new InsiderMaterialityWeights().CanonicalDescriptor(),
            provider.GetRequiredService<InsiderMaterialityWeights>().CanonicalDescriptor());
        Assert.Equal(new MediaCollapseOptions(), provider.GetRequiredService<MediaCollapseOptions>());
        Assert.Same(
            AttentionSourceTierOptions.Default, provider.GetRequiredService<AttentionSourceTierOptions>());

        var strategies = provider.GetRequiredService<ScoringStrategySet>();
        Assert.Equal("default", strategies.Primary.Name);
        Assert.Equal(new ScoringWeights(), strategies.Primary.Weights);
    }

    [Theory]
    [MemberData(nameof(ShippedOverlays))]
    public void EveryShippedOverlay_ComposedOntoDefaultAsRunRadarDoes_PassesTheGuards(string overlay)
    {
        // The guards must reject typos, not the profiles we actually run: every committed overlay, composed
        // exactly as run-radar.ps1 composes it, binds every guarded site without a single throw.
        using var provider = BindAllGuardedSites(Compose(overlay));

        Assert.NotNull(provider.GetRequiredService<ScoringWeights>());
        Assert.NotNull(provider.GetRequiredService<ScoringStrategySet>());
        Assert.NotNull(provider.GetRequiredService<InsiderMaterialityWeights>());
        Assert.NotNull(provider.GetRequiredService<MediaCollapseOptions>());
        Assert.NotNull(provider.GetRequiredService<AttentionSourceTierOptions>());
    }

    [Fact]
    public void LowMediaOverlay_StillResolvesTheExperimentsMagnitude_ByteIdentically()
    {
        // The worked example (and the regression DefaultRunProfileTests pins from the Worker side): the
        // low-media experiment's single magnitude must still reach both the ambient weights and the primary
        // strategy after the guards — the guards accept-or-reject, they never change a valid resolution.
        using var provider = BindAllGuardedSites(Compose("low-media"));

        Assert.Equal(0.05, provider.GetRequiredService<ScoringWeights>().MediaReachWeight);

        var primary = provider.GetRequiredService<ScoringStrategySet>().Primary;
        Assert.Equal("low-media", primary.ScoringProfile);
        Assert.Equal(0.05, primary.Weights.MediaReachWeight);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 187 §6 — the `_comment*` annotation boundary, at the exact place the live run crashed
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE REGRESSION BEING CLOSED. On 2026-08-23 the scheduled baseline run crashed at startup because
    /// <c>run-radar.ps1</c> skipped only the EXACT key <c>_comment</c> while the profile had grown a
    /// <c>_comment2</c> note, which reached the Worker's strict config-key allowlist as
    /// <c>--Radar:NewsResearch:_comment2=…</c>. The whole test suite was green throughout, because this
    /// mirror had the same bug — so the fixture below pins the CONVENTION (<c>_comment</c>,
    /// <c>_comment2</c>, <c>_comment3</c>, at the root AND nested, in any casing) rather than one key name.
    /// </summary>
    [Fact]
    public void Flatten_SkipsEveryCommentStarKey_AtRootAndNested_WhileKeepingItsRealSiblings()
    {
        const string fixture = """
            {
              "_comment": "root note",
              "_comment2": "root note 2",
              "_Comment3": "root note 3, oddly cased",
              "Radar": {
                "_comment": "section note",
                "_comment2": "section note 2",
                "_comment3": "section note 3",
                "CaptureRss": true,
                "Nested": {
                  "_comment2": "deep note",
                  "Value": 7,
                  "List": [
                    { "_comment": "entry note", "Name": "first" }
                  ]
                }
              }
            }
            """;

        var flattened = RunProfileMirror.FlattenJson(fixture);

        // Not one annotation key survives, at any depth or casing…
        Assert.DoesNotContain(
            flattened.Keys,
            key => key.Contains(RunProfileMirror.CommentKeyPrefix, StringComparison.OrdinalIgnoreCase));

        // …and every real sibling does, with its value intact (the skip must not eat the section).
        Assert.Equal("true", flattened["Radar:CaptureRss"]);
        Assert.Equal("7", flattened["Radar:Nested:Value"]);
        Assert.Equal("first", flattened["Radar:Nested:List:0:Name"]);
    }

    /// <summary>
    /// The same convention, asserted against the REAL committed profiles rather than a fixture: the shipped
    /// files genuinely carry multi-note sections (<c>Radar:NewsResearch</c> has <c>_comment</c> AND
    /// <c>_comment2</c>), and not one of those annotations may become a Worker configuration key.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComposableProfiles))]
    public void NoCommentKeyEverReachesTheComposedConfiguration(string? overlayProfileName)
    {
        var merged = RunProfileMirror.Compose(overlayProfileName);

        Assert.DoesNotContain(
            merged.Keys,
            key => key.Contains(RunProfileMirror.CommentKeyPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
