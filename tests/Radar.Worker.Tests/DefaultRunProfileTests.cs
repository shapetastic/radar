using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Radar.Application.Scoring;
using Radar.Domain.Signals;
using Radar.Infrastructure.DependencyInjection;

namespace Radar.Worker.Tests;

/// <summary>
/// Guards the committed baseline run profile (<c>scripts/run-profiles/default.json</c>) — the canonical record
/// of HOW we run a live measurement. These are pure file/JSON assertions: no Worker is started, no HTTP or AI
/// call is made, and no API key is read (only the env-var NAME the profile declares is asserted).
/// </summary>
public sealed class DefaultRunProfileTests
{
    private static JsonElement DefaultProfileAi()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DefaultProfilePath()));
        return doc.RootElement.GetProperty("Radar").GetProperty("Ai").Clone();
    }

    /// <summary>
    /// Walks up from the test binary to the repo root (the first ancestor carrying
    /// <c>scripts/run-profiles/default.json</c>) so the test does not depend on the working directory.
    /// </summary>
    private static string DefaultProfilePath() => ProfilePath("default");

    /// <summary>
    /// Resolves any committed run profile by name, walking up from the test binary to the repo root (the first
    /// ancestor carrying <c>scripts/run-profiles/</c>) so the test does not depend on the working directory.
    /// </summary>
    private static string ProfilePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "run-profiles", name + ".json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate scripts/run-profiles/{name}.json from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void DefaultProfile_UsesDeepInfraDeepSeekEarningsReader()
    {
        // Spec 119: the baseline earnings read is DeepSeek-V4-Flash on DeepInfra via the OpenAI-compatible
        // provider (spec 118). Pinned so the baseline model cannot drift silently — the model is folded into the
        // AI-ON scoring fingerprint by value, so a change here is a comparability event, not a detail.
        var ai = DefaultProfileAi();

        Assert.Equal("openai", ai.GetProperty("Provider").GetString());
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash", ai.GetProperty("Model").GetString());

        var openAi = ai.GetProperty("OpenAi");
        Assert.Equal("https://api.deepinfra.com/v1/openai", openAi.GetProperty("BaseUrl").GetString());
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash", openAi.GetProperty("Model").GetString());
        Assert.Equal("DEEPINFRA_API_KEY", openAi.GetProperty("ApiKeyEnvVar").GetString());
    }

    [Fact]
    public void DefaultProfile_DeclaresOnlyTheKeyEnvVarName_NeverAKeyValue()
    {
        // Secret hygiene (same precedent as the SEC User-Agent): the committed profile may name the environment
        // variable but must never carry an inline key. Assert there is no ApiKey-style property anywhere under
        // Radar:Ai other than the env-var NAME field.
        var ai = DefaultProfileAi();
        var openAi = ai.GetProperty("OpenAi");

        Assert.False(openAi.TryGetProperty("ApiKey", out _));
        Assert.False(ai.TryGetProperty("ApiKey", out _));
        Assert.Equal(
            JsonValueKind.Undefined,
            ai.TryGetProperty("Anthropic", out var anthropic) ? anthropic.ValueKind : JsonValueKind.Undefined);
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 154 — the baseline CONTROL strategies the profile ships
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Binds the committed profile exactly as a live run would — same JSON, same
    /// <see cref="InfrastructureServiceCollectionExtensions.AddRadarScoringStrategies"/> — so a malformed or
    /// unbalanced strategy in the profile fails HERE rather than at the start of a measurement run.
    /// </summary>
    private static ScoringStrategySet DefaultProfileStrategies() => ProfileStrategies(BindProfiles());

    /// <summary>
    /// Binds <c>default.json</c> and then, optionally, an overlay profile on top of it — exactly as
    /// <c>scripts/run-radar.ps1</c> does. The script flattens each profile's JSON into <c>Radar:A:B:C</c> keys
    /// (arrays become <c>…:0</c>, <c>…:1</c>, …) and merges the overlay into the base <b>one flat key at a
    /// time</b> (<c>$merged[$k] = $overlay[$k]</c>), which is precisely the shallow, per-key, last-source-wins
    /// precedence a chained <c>AddJsonFile</c> gives — the JSON configuration provider flattens arrays into
    /// the same indexed keys.
    /// </summary>
    private static IConfigurationRoot BindProfiles(string? overlayProfile = null)
    {
        var builder = new ConfigurationBuilder().AddJsonFile(DefaultProfilePath(), optional: false);
        if (overlayProfile is not null)
        {
            builder = builder.AddJsonFile(ProfilePath(overlayProfile), optional: false);
        }

        return builder.Build();
    }

    private static ScoringStrategySet ProfileStrategies(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddRadarScoringStrategies(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ScoringStrategySet>();
    }

    [Fact]
    public void DefaultProfile_ShipsThePrimaryPlusThreeNamedBaselines()
    {
        // Spec 154 §3: every control is prefixed `baseline-` so nobody reads one as a candidate strategy in a
        // report or a leaderboard — and the PRIMARY (the series the weekly report renders) is not one.
        var set = DefaultProfileStrategies();

        // The three controls ship ALONGSIDE — not instead of — the composite strategies this profile declares
        // (the spec-149 notedness curve). Asserted as the `baseline-` SUBSET rather than as the whole strategy
        // list: adding or retiring a composite arm is an ordinary config change and must not fail this test,
        // whereas a control silently disappearing would invalidate every AD-15 "beats the baseline" claim made
        // against it. Order is pinned too — these are the entries a reader compares against, in the order the
        // profile's own `_comment` describes them.
        Assert.Equal(
            ["baseline-earnings-only", "baseline-activity-only", "baseline-media-only"],
            set.Strategies
                .Where(s => s.Name.StartsWith("baseline-", StringComparison.Ordinal))
                .Select(s => s.Name));

        Assert.Equal("default", set.Primary.Name);
        Assert.DoesNotContain(
            "baseline-", set.Primary.Name, StringComparison.Ordinal);

        // A control exists to be BEATEN, so it must never be the series the weekly report leads with.
        Assert.All(
            set.Strategies.Where(s => s.Name.StartsWith("baseline-", StringComparison.Ordinal)),
            s => Assert.False(s.IsPrimary));

        // baseline-earnings-only is CONFIG-ONLY — the shipped v8 over one signal type, no new code.
        var earnings = set.Strategies.Single(s => s.Name == "baseline-earnings-only");
        Assert.Equal(ScoreFormulaVersions.V8, earnings.Formula);
        Assert.Equal(SignalTypeFilter.Create([SignalType.GuidanceChange]), earnings.SignalTypes);

        // The two activity controls run the direction-free control formula, each over its own budget.
        foreach (var name in new[] { "baseline-activity-only", "baseline-media-only" })
        {
            var strategy = set.Strategies.Single(s => s.Name == name);
            Assert.Equal(ScoreFormulaVersions.BaselineActivityV1, strategy.Formula);
            var channel = Assert.Single(strategy.Channels.Channels);
            Assert.Equal(ScoringChannelKind.Collector, channel.Kind);
            Assert.Equal(1.0, channel.Weight);
        }
    }

    [Fact]
    public void DefaultProfile_EveryChannelCollectorName_IsOneThisProfileActuallyEnables()
    {
        // THE TYPO CATCHER, against the REAL file. Channel collectors are matched EXACTLY (ordinal) against
        // IEvidenceCollector.CollectorName — NOT against the Radar:Collectors KIND tokens — so a plausible
        // mistake such as "rss" or "NewsSearch" would name a collector that never runs, silently costing that
        // channel its whole share. Resolved through the ONE kind ↦ name table the Worker registers from
        // (spec 147), so this check and the live vocabulary cannot drift.
        using var doc = JsonDocument.Parse(File.ReadAllText(DefaultProfilePath()));
        var kinds = doc.RootElement
            .GetProperty("Radar")
            .GetProperty("Collectors")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enabledNames = RadarWorkerServices.CollectorKindTable
            .Where(entry => kinds.Contains(entry.Kind))
            .Select(entry => entry.CollectorName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(kinds.Count, enabledNames.Count);

        var declared = DefaultProfileStrategies().Strategies
            .SelectMany(s => s.Channels.Channels)
            .SelectMany(c => c.Collectors)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.All(declared, name => Assert.Contains(name, enabledNames, StringComparer.Ordinal));

        // The whole-activity control must budget for EVERY enabled collector — that is what makes it the
        // "is the score just 'something happened'?" baseline rather than an arbitrary subset.
        var activity = Assert.Single(
            DefaultProfileStrategies().Strategies
                .Single(s => s.Name == "baseline-activity-only")
                .Channels.Channels);
        Assert.Equal(
            enabledNames.Order(StringComparer.Ordinal), activity.Collectors.Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 176 — explicit strategy purpose + the entry-level key allowlist, against the REAL shipped files
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DefaultProfile_MarksExactlyTheFourComparators_EverythingElseIsResearch()
    {
        // Spec 176: purpose is EXPLICIT metadata, never inferred from a `baseline-` prefix or `-control`
        // suffix — so the shipped set is pinned by name. The four comparators are the three spec-154
        // baselines plus the spec-157 matched v10 control; every other arm (including the primary) defaults
        // to Research and does not write the value redundantly.
        var set = DefaultProfileStrategies();

        Assert.Equal(
            [
                "baseline-earnings-only", "baseline-activity-only", "baseline-media-only",
                "disclosure-led-v10-control",
            ],
            set.Strategies
                .Where(s => s.Purpose == StrategyPurpose.Comparator)
                .Select(s => s.Name));

        Assert.Equal(StrategyPurpose.Research, set.Primary.Purpose);
        Assert.All(
            set.Strategies.Where(s => s.Purpose == StrategyPurpose.Comparator),
            s => Assert.False(s.IsPrimary));
    }

    [Fact]
    public void ShippedProfiles_RemainCleanUnderTheStrategyEntryAllowlist()
    {
        // The spec-176 entry-level guard fails fast on any unknown Radar:Strategies[i] key. Binding each
        // shipped profile through the REAL AddRadarScoringStrategies proves the committed files carry only
        // the seven valid keys — and that the guard cannot break a live measurement run.
        Assert.NotNull(ProfileStrategies(BindProfiles()));
        Assert.NotNull(ProfileStrategies(BindProfiles("low-media")));
        Assert.NotNull(ProfileStrategies(BindProfiles("long-window")));
    }

    // ---------------------------------------------------------------------------------------------------
    // Spec 157 — the matched disclosure-led pair (the predeclared spec-157 §7 / AD-16 budget)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DefaultProfile_ShipsTheDisclosureLedV11Arm_AndItsMatchedV10Control()
    {
        // Spec 157 §7 (as amended after spec 158 and the post-merge observability review) PREDECLARES this
        // pair — the budget is not an implementer's choice, and AD-16's precommitted outcome reads the
        // disclosure-led-v11 snapshot, so the arm silently changing shape would corrupt the screen. Both arms
        // must carry the IDENTICAL budget: that is what makes any ranking difference attributable to
        // directional-only versus all-signal collector saturation and to nothing else.
        var set = DefaultProfileStrategies();

        var v11 = set.Strategies.Single(s => s.Name == "disclosure-led-v11");
        var control = set.Strategies.Single(s => s.Name == "disclosure-led-v10-control");

        Assert.Equal(ScoreFormulaVersions.V11, v11.Formula);
        Assert.Equal(ScoreFormulaVersions.V10, control.Formula);
        Assert.False(v11.IsPrimary);
        Assert.False(control.IsPrimary);

        foreach (var arm in new[] { v11, control })
        {
            // ONE sec-edgar channel at the whole budget — spec 158's measured option A (43/43 companies have
            // a sec-edgar source; RSS only 26/43, so a press share would conflate missing configuration with
            // valid quiet). And NO breadth channel: neither arm may declare one, v11 because its formula
            // rejects it (spec 158) and the control because the budgets must stay identical.
            var channel = Assert.Single(arm.Channels.Channels);
            Assert.Equal("filings", channel.Name);
            Assert.Equal(ScoringChannelKind.Collector, channel.Kind);
            Assert.Equal(["sec-edgar"], channel.Collectors);
            Assert.Equal(1.00, channel.Weight);
            Assert.Equal(3.0, channel.Saturation);
        }

        // Identical budgets, asserted as value equality of the canonical channel set — the same equality the
        // fingerprint's channels= segment is built from, so "identical" here means identical where it counts.
        Assert.Equal(v11.Channels, control.Channels);
    }

    // ---------------------------------------------------------------------------------------------------
    // The OPERATOR OBLIGATION spec 154 created: an overlay profile must re-point the primary's ScoringProfile
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE REGRESSION THIS SLICE ALMOST SHIPPED. Before <c>default.json</c> declared
    /// <c>Radar:Strategies</c> at all (commit <c>168125b</c>, which added the notedness-curve arms — spec 154
    /// only widened that list), the single strategy Radar SYNTHESISES inherited the <b>ambient</b>
    /// <c>Radar:Scoring:Profile</c> — the exact key <c>low-media.json</c> overlays — and the experiment's
    /// weights reached the primary for free. Declaring the list explicitly moved that decision onto each
    /// entry's own <c>ScoringProfile</c>, so an overlay that changes only <c>Radar:Scoring:Profile</c> now
    /// leaves the primary on the code defaults while STILL writing to <c>data/experiments/low-media/</c>,
    /// STILL logging "run profile: low-media" and STILL stamping the baseline's <c>ScoringConfigVersion</c> —
    /// numbers that look like the experiment ran when it did not. That fail-open shape is exactly what specs
    /// 138 and 149 each had to close once.
    /// <para>
    /// So <c>low-media.json</c> carries the strategy-level delta too, and this test binds both files the way
    /// <c>run-radar.ps1</c> does and asserts the primary actually scores at the experiment's magnitude.
    /// </para>
    /// </summary>
    [Fact]
    public void LowMediaOverlay_ActuallyReachesThePrimaryStrategysWeights()
    {
        var primary = ProfileStrategies(BindProfiles("low-media")).Primary;

        // The headline number first: this is the magnitude the whole experiment exists to vary.
        Assert.Equal(0.05, primary.Weights.MediaReachWeight);
        Assert.Equal("default", primary.Name);
        Assert.Equal("low-media", primary.ScoringProfile);

        // …and the baseline run is genuinely the thing it is being compared against.
        Assert.Equal(0.10, DefaultProfileStrategies().Primary.Weights.MediaReachWeight);
    }

    /// <summary>
    /// Pins the mechanism the fix relies on: because <c>run-radar.ps1</c> merges ONE FLAT KEY AT A TIME, an
    /// overlay entry carrying only <c>ScoringProfile</c> overrides <c>Radar:Strategies:0:ScoringProfile</c> and
    /// nothing else — the primary keeps its <c>Name</c> from <c>default.json</c>, and the three
    /// <c>baseline-</c> CONTROLS at indices 5-7 are untouched. Controls staying identical across the baseline
    /// and the experiment is what makes the two runs comparable at all, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void LowMediaOverlay_ChangesOnlyThePrimarysProfile_LeavingTheControlsIdentical()
    {
        var baseline = ProfileStrategies(BindProfiles());
        var experiment = ProfileStrategies(BindProfiles("low-media"));

        Assert.Equal(baseline.Strategies.Select(s => s.Name), experiment.Strategies.Select(s => s.Name));

        foreach (var control in experiment.Strategies.Where(s => s.Name.StartsWith("baseline-", StringComparison.Ordinal)))
        {
            var original = baseline.Strategies.Single(s => s.Name == control.Name);
            Assert.Equal(original.ScoringProfile, control.ScoringProfile);
            Assert.Equal(original.Weights, control.Weights);
            Assert.Equal(original.Formula, control.Formula);
            Assert.Equal(original.SignalTypes, control.SignalTypes);
            Assert.Equal(
                original.Channels.Channels.Select(c => (c.Name, c.Kind, c.Weight, c.Saturation)),
                control.Channels.Channels.Select(c => (c.Name, c.Kind, c.Weight, c.Saturation)));
        }
    }

    /// <summary>
    /// <c>long-window.json</c> overlays only <c>Radar:ScoringWindowDays</c> — a GLOBAL
    /// <see cref="ScoringOptions"/> knob that reaches every strategy through the pipeline rather than through a
    /// per-strategy <c>ScoringProfile</c> — so unlike <c>low-media</c> it needs no strategy-level delta. Pinned
    /// here so the distinction stays deliberate: the obligation applies to overlays that change scoring
    /// <b>weights</b>, not to every overlay.
    /// </summary>
    [Fact]
    public void LongWindowOverlay_NeedsNoStrategyDelta_BecauseTheWindowIsNotAPerStrategyWeight()
    {
        var configuration = BindProfiles("long-window");

        Assert.Equal("120", configuration["Radar:ScoringWindowDays"]);

        var baseline = ProfileStrategies(BindProfiles());
        var experiment = ProfileStrategies(configuration);

        Assert.Equal(baseline.Strategies.Select(s => s.Name), experiment.Strategies.Select(s => s.Name));
        Assert.All(
            experiment.Strategies,
            s => Assert.Equal(baseline.Strategies.Single(b => b.Name == s.Name).Weights, s.Weights));
    }
}
