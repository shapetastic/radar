using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.Attention;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Infrastructure.Tests.DependencyInjection;

/// <summary>
/// Spec 174's load-bearing compatibility gate: the new config-binder guards must reject TYPOS, never the
/// profiles Radar actually runs. Every shipped <c>scripts/run-profiles/*.json</c> overlay, composed onto
/// <c>default.json</c> exactly the way <c>run-radar.ps1</c> composes them, must pass every guarded binder —
/// and the guarded sites must resolve byte-identically to their pre-174 values.
/// <para>
/// The composition is mirrored faithfully rather than approximated: the script flattens each profile's JSON
/// into <c>Radar:A:B:C</c> keys (arrays become <c>…:0</c>, <c>…:1</c>, …), SKIPS every <c>_comment</c> key at
/// any depth, and merges the overlay into the base ONE FLAT KEY AT A TIME (<c>$merged[$k] = $overlay[$k]</c>,
/// overlay wins), then feeds the merged dictionary to the Worker as <c>--Radar:…=value</c> command-line args.
/// The script's remaining additions (output-directory overrides, the SEC User-Agent, <c>Radar:RunMode</c>)
/// touch none of the guarded sections, so they are not replayed here.
/// </para>
/// </summary>
public sealed class RunProfileGuardCompatibilityTests
{
    /// <summary>
    /// Walks up from the test binary to the repo root (the first ancestor carrying
    /// <c>scripts/run-profiles/</c>) so the test does not depend on the working directory — the same
    /// resolution <c>DefaultRunProfileTests</c> uses.
    /// </summary>
    private static string ProfilesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "run-profiles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate scripts/run-profiles/ from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Mirrors <c>run-radar.ps1</c>'s <c>Add-Flattened</c>: leaf values become flat <c>A:B:C</c> config keys,
    /// arrays become indexed keys, and <c>_comment</c> keys are skipped at every depth.
    /// </summary>
    private static void Flatten(JsonElement node, string prefix, Dictionary<string, string?> acc)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "_comment")
                    {
                        continue;
                    }

                    Flatten(
                        property.Value,
                        prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}",
                        acc);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{index}", acc);
                    index++;
                }

                break;

            case JsonValueKind.True:
                acc[prefix] = "true";
                break;

            case JsonValueKind.False:
                acc[prefix] = "false";
                break;

            case JsonValueKind.Null:
                acc[prefix] = string.Empty;
                break;

            default:
                // Numbers render as their raw (invariant) JSON text, matching the script's invariant
                // ToString; strings render as their value.
                acc[prefix] = node.ToString();
                break;
        }
    }

    private static Dictionary<string, string?> FlattenProfile(string fileName)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ProfilesDirectory(), fileName)));
        var flattened = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Flatten(doc.RootElement, string.Empty, flattened);
        return flattened;
    }

    /// <summary>Composes default.json plus an optional overlay, overlay winning one flat key at a time.</summary>
    private static IConfiguration Compose(string? overlayFileName)
    {
        var merged = FlattenProfile("default.json");
        if (overlayFileName is not null)
        {
            foreach (var (key, value) in FlattenProfile(overlayFileName))
            {
                merged[key] = value; // profile wins, per flattened key — exactly the script's merge
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(merged).Build();
    }

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
        foreach (var file in Directory.GetFiles(ProfilesDirectory(), "*.json")
                     .Select(Path.GetFileName)
                     .Where(name => !string.Equals(name, "default.json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(file!);
        }

        return data;
    }

    [Fact]
    public void DefaultProfile_PassesEveryGuard_AndResolvesTheGuardedSitesByteIdentically()
    {
        using var provider = BindAllGuardedSites(Compose(overlayFileName: null));

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
        using var provider = BindAllGuardedSites(Compose("low-media.json"));

        Assert.Equal(0.05, provider.GetRequiredService<ScoringWeights>().MediaReachWeight);

        var primary = provider.GetRequiredService<ScoringStrategySet>().Primary;
        Assert.Equal("low-media", primary.ScoringProfile);
        Assert.Equal(0.05, primary.Weights.MediaReachWeight);
    }
}
