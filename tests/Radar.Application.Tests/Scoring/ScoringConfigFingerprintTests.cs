using System.Globalization;
using System.Reflection;
using Radar.Application.Scoring;
using Radar.Application.SignalExtraction;
using Radar.Infrastructure.Attention;

namespace Radar.Application.Tests.Scoring;

public sealed class ScoringConfigFingerprintTests
{
    // The canonical descriptor of the default attention tier map (spec 88 seed lists). Application.Tests
    // already references Infrastructure (AD-4), so the real ConfiguredAttentionSourceWeights can produce it.
    private static string DefaultTierDescriptor() =>
        new ConfiguredAttentionSourceWeights(AttentionSourceTierOptions.Default).CanonicalDescriptor();

    // The signal-source IDENTITY descriptor of the default run profile (spec 95, narrowed by spec 141): the
    // extractor rule-set identity, canonicalized. It is folded into the fingerprint after the attention
    // descriptor, so the default fingerprint value depends on it.
    //
    // SPEC 141 REMOVED THE COLLECTOR CSV FROM THIS STRING. Until this slice it also carried
    // `collectors=RssPressReleaseCollector,fda,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending;` — the
    // 7-collector default — which meant enabling an eighth collector re-stamped every strategy's identity
    // even when its scores were bit-for-bit identical. The enabled-collector set is now recorded per-snapshot
    // as `CollectionProvenance` (see SignalSourceDescriptorTests) and hashed into NOTHING, so it is correctly
    // absent here. The rule-set identity is UNCHANGED at radar-keyword-rules-v6 (spec 130 added the
    // TrademarkActivity group; spec 129 added RegulatoryApproval; spec 127 added PatentActivity; spec 103
    // added HiringActivity).
    private const string SourceDescriptor = "rules=radar-keyword-rules-v6;";

    // The insider-materiality descriptor of the default config (spec 96): the config-tunable buy/sell tiers +
    // cluster boost, folded into the fingerprint after the signal-source descriptor. Computed from the record
    // so it can't drift from the code default (== spec 93).
    private static readonly string InsiderDescriptor = new InsiderMaterialityWeights().CanonicalDescriptor();

    // The media-collapse descriptor of the default config (spec 109): the same-event media-attention collapse
    // structure (media-collapse-v1) + the tunable window (default 3 days), folded into the fingerprint after
    // the insider-materiality descriptor. Computed from the default so it can't drift from the code default.
    private static readonly string MediaCollapseDescriptor =
        new MediaAttentionCollapse(new MediaCollapseOptions()).CanonicalDescriptor();

    // The recent-signal window of the default config (spec 148), the LAST hashed field. Taken from
    // ScoringOptions rather than written as a literal 30 days so the pins below cannot silently disagree with
    // the code default: if someone changes the default window, the pinned values fail here rather than in a
    // live run six weeks later.
    private static readonly TimeSpan DefaultWindow = new ScoringOptions().Window;

    /// <summary>
    /// The default-config fingerprint, with every hashed field supplied from the code defaults. One helper so
    /// the ~20 call sites below cannot drift from each other.
    /// </summary>
    private static string DefaultFingerprint(
        string? formulaVersion = null,
        ScoringWeights? weights = null,
        string? attentionDescriptor = null,
        string? sourceDescriptor = null,
        string? insiderDescriptor = null,
        string? mediaCollapseDescriptor = null,
        TimeSpan? window = null) =>
        ScoringConfigFingerprint.Compute(
            "mvp-engine-v1",
            formulaVersion ?? "radar-formula-v8",
            weights ?? new ScoringWeights(),
            attentionDescriptor ?? DefaultTierDescriptor(),
            sourceDescriptor ?? SourceDescriptor,
            insiderDescriptor ?? InsiderDescriptor,
            mediaCollapseDescriptor ?? MediaCollapseDescriptor,
            window ?? DefaultWindow);

    [Fact]
    public void Compute_SameInputs_ProduceSameFingerprint()
    {
        Assert.Equal(DefaultFingerprint(), DefaultFingerprint());
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = DefaultFingerprint();

        const string prefix = "radar-scoring-fp-";
        Assert.StartsWith(prefix, fp, StringComparison.Ordinal);

        var hex = fp[prefix.Length..];
        Assert.Equal(12, hex.Length);
        Assert.All(hex, ch => Assert.True(Uri.IsHexDigit(ch) && !char.IsUpper(ch), $"'{ch}' must be lowercase hex"));
    }

    [Fact]
    public void Compute_IsCultureInvariant()
    {
        var invariant = DefaultFingerprint();

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting — including the
            // spec-148 window field, whose tick count is large enough for a locale group separator to matter.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, DefaultFingerprint());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Compute_DefaultConfig_MatchesPinnedFingerprint()
    {
        // THIS PIN IS A CHANGE-DETECTOR, NOT AN INVARIANT (spec 141).
        //
        // Moving it is a NORMAL, INTENDED act that requires a conscious update in the same slice — it is not
        // "scope leakage". Its job is to make a fingerprint move IMPOSSIBLE TO MAKE ACCIDENTALLY: an
        // unnoticed default-weight, default-tier, rule-set, insider-materiality, media-collapse or scoring-
        // window drift fails here, and the author must then decide whether the move was intended and record
        // its lineage.
        //
        // What the pin no longer does is pretend the fingerprint never changes. It had already changed 17
        // times over 851 live snapshots (11 radar-scoring-fp-* + 6 legacy radar-scoring-config-vN); the
        // largest cohort is 133 snapshots ≈ 3 runs, and the spec-141 AI-ON value below had exactly 43 — one
        // single run. The score series is keyed by StrategyName now (ScoreSeriesKey), so a pin move no longer
        // fragments anything; it re-stamps recorded provenance and trips StrategyIdentityGuard, which is
        // exactly what it should do.
        //
        // Lineage: spec 133 (radar-scoring-fp-6b2f468041b9 — the 7-collector default) → spec 141, which
        // removed the enabled-collector CSV from the hashed identity altogether
        // (radar-scoring-fp-2ce20f8fc497) → SPEC 148, which folds in the two remaining output-affecting
        // inputs that had been hashed into NOTHING: the recent-signal WINDOW (a 14-day and a 30-day run
        // produce materially different Trajectory/SignalVelocity/Attention yet stamped the same value) and
        // ScoringWeights.TrajectoryCorroborationK (the v8 Trajectory denominator, and since spec 146 the v9
        // channel direction factor's denominator too). THE MOVE IS THE DELIVERABLE, per AD-10 as amended by
        // spec 141. Scoring math is byte-identical — no _formula.Version bump, no RuleSetVersion bump, not a
        // single weight edited (asserted separately by ScoringEngineTests); only the stamp differs.
        //
        // SPEC 146 DELIBERATELY DID NOT MOVE THIS PIN. It added a per-strategy Formula and a channel budget,
        // and it extracted v8's per-signal primitives into the shared ScoreSignalMath — but the default
        // strategy still names radar-formula-v8, still declares no channels (ScoringChannelSet.Empty folds in
        // as a verbatim passthrough), and the extraction preserved v8's expression shapes and accumulation
        // order, so both the hashed inputs and the scores are unchanged.
        //
        // ⚠ SPEC 148 BROKE THE "PIN == LIVE STAMP" EQUIVALENCE, and that is worth stating where the pin is.
        // Every earlier slice's pin doubled as the value a live baseline run stamps, because every hashed
        // input was a code default. The window is not: this pin is computed at the ScoringOptions CODE
        // DEFAULT of 30 days (DefaultWindow, above), while the live baseline runs at
        // Radar:ScoringWindowDays = 60 (RadarWorkerOptions/appsettings.json; scripts/run-profiles/default.json
        // does not override it) and therefore stamps radar-scoring-fp-4eb2fe5d3cdf. The live pair is recorded
        // in default.json's own comment, which is the operator-facing record; this pin is the unit-level
        // change-detector. Both are correct at their own window — do not "reconcile" them.
        Assert.Equal("radar-scoring-fp-0c46e07b94db", DefaultFingerprint());
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { AttentionHalfSaturation = 12.0 }));
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(attentionDescriptor: "unknown=0.9;"));
    }

    [Fact]
    public void Compute_ChangedExtractorRuleSet_ChangesFingerprint()
    {
        // RETARGETED BY SPEC 141. This test used to assert that DROPPING A COLLECTOR re-stamps the
        // fingerprint (spec 95). That is now the OPPOSITE of the intended behaviour — a collector toggle must
        // leave a strategy's identity untouched, which SignalSourceDescriptorTests asserts directly at the
        // source. What remains true, and is what this test now guards, is that a change to the signal-source
        // IDENTITY descriptor — the extractor rule STRUCTURE identity, which does change what is scored —
        // still re-stamps.
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(sourceDescriptor: "rules=radar-keyword-rules-v7;"));
    }

    [Fact]
    public void Compute_ChangedInsiderTiers_ChangesFingerprint()
    {
        // Changing an insider tier (or the cluster boost) changes the effective scoring config, so the
        // fingerprint must re-stamp automatically (spec 96 — magnitudes hashed by value, no RuleSetVersion bump).
        var changedInsider = new InsiderMaterialityWeights { ClusterBoost = 2 }.CanonicalDescriptor();

        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(insiderDescriptor: changedInsider));
    }

    // The directional-filing source's own descriptor for the default live run (pinned field-for-field by
    // DirectionalFilingSignalSourceTests.ScoringDescriptor_EncodesPerSignalMagnitudes_InCanonicalForm): the default
    // Strength/Novelty/MinConfidence == 8/6/0.6 (spec-112 Strength 6→8 recalibration) plus the spec-119
    // earnings-read model identity — scripts/run-profiles/default.json now configures the DeepInfra
    // OpenAI-compatible provider with deepseek-ai/DeepSeek-V4-Flash, and the Worker composes the identity as
    // "{provider}:{effective model}".
    private const string AiDirectionalDescriptor =
        "directional-filing:str=8;nov=6;minconf=0.6;model=openai:deepseek-ai/DeepSeek-V4-Flash";

    // The AI-ON signal-source descriptor (spec 106): the AI-OFF SourceDescriptor with the directional-filing
    // descriptor appended as an ESCAPED ai=… segment. Built through the real DescriptorEscaping (not a hand-written
    // literal) so this is byte-identical to what SignalSourceDescriptor actually produces when the opt-in AI path is
    // registered — the pre-spec-119 literal omitted that escaping, so the old AI-ON pin was not the value a live
    // AI-ON run stamped; spec 119 corrects that at the same time as folding the model in.
    private static readonly string AiOnSourceDescriptor =
        SourceDescriptor + $"ai={DescriptorEscaping.Escape(AiDirectionalDescriptor)};";

    [Fact]
    public void Compute_AiOnSourceDescriptor_DiffersFromAiOff()
    {
        // Enabling the AI directional-filing path widens the signal-production surface (it emits directional
        // GuidanceChange signals), so the fingerprint MUST re-stamp — closing the AD-10 comparability gap between
        // an AI-on and an AI-off run (the AI analogue of spec 95's secform4 fix).
        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor));
    }

    [Fact]
    public void Compute_AiOnDefault_MatchesPinnedFingerprint()
    {
        // The AI-ON default fingerprint AT THE ScoringOptions CODE-DEFAULT 30-DAY WINDOW: with an AI provider
        // registered (as scripts/run-profiles/default.json configures), the AI directional-filing descriptor is
        // folded in (AiOnSourceDescriptor above), so the effective config differs from the AI-OFF pin. Pinned so
        // an accidental drift in the AI directional magnitudes, the earnings-read model, or any other folded
        // input is caught for the AI-ON run too.
        //
        // ⚠ NOT the live stamp any more — see the AI-OFF pin above. Since spec 148 the window is hashed, and
        // the live baseline runs at Radar:ScoringWindowDays = 60, where the AI-ON value is
        // radar-scoring-fp-4da4b5ff6ec9 (recorded in default.json's comment). This pin is the unit-level
        // change-detector at the code default; that one is the operator-facing live record.
        //
        // A CHANGE-DETECTOR, NOT AN INVARIANT — see the AI-OFF pin above for why a deliberate move is normal.
        // This particular value is the sharpest evidence for that position: the spec-141 value it replaces had
        // exactly 43 snapshots on the live store, i.e. ONE RUN of history.
        //
        // Lineage: spec 112 (radar-scoring-fp-454984785732) → spec 117 radar-formula-v7 structure bump +
        // following-discount weights (radar-scoring-fp-4c06fd2d2d8c) → spec 119, which folded the earnings-read
        // model identity into the directional descriptor by value and built the ai= segment through the real
        // escaping (radar-scoring-fp-2ef5ef96cce2) → spec 122, the radar-formula-v8 structure bump + the new
        // CollapsedBreadthCredit magnitude, which re-stamps BOTH the AI-OFF and the AI-ON default
        // (radar-scoring-fp-c908f03a554a) → spec 127, the RuleSetVersion v3→v4 bump for the new PatentActivity
        // rule group (opt-in OFF) (radar-scoring-fp-63c096e531ec) → spec 129, the RuleSetVersion v4→v5 bump for
        // the new RegulatoryApproval rule group (opt-in-OFF openFDA collector) (radar-scoring-fp-2be98e738684)
        // → spec 130, the RuleSetVersion v5→v6 bump for the new TrademarkActivity rule group (opt-in-OFF USPTO
        // trademark collector), which folds into BOTH defaults automatically with scoring math byte-identical
        // (radar-scoring-fp-74c5e077f728) → spec 133, which promotes the openFDA collector `fda` into
        // scripts/run-profiles/default.json: a COLLECTOR-SET change (6 → 7 collectors) that re-stamped
        // automatically (radar-scoring-fp-57356123e09b) → spec 141, which removed the collector CSV from the
        // hashed identity entirely, so a re-stamp like spec 133's can never happen again
        // (radar-scoring-fp-3457da53489d) → SPEC 148, which folds in the recent-signal WINDOW and
        // ScoringWeights.TrajectoryCorroborationK — two output-affecting inputs that were hashed into nothing.
        // THE MOVE IS THE DELIVERABLE; scoring math is byte-identical, with no _formula.Version or
        // RuleSetVersion bump. → SPEC 146 deliberately did NOT move it: see the
        // AI-OFF pin above for why the per-strategy Formula/Channels addition folds in here as a no-op.
        Assert.Equal(
            "radar-scoring-fp-28226897f97b",
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor));
    }

    [Fact]
    public void Compute_ChangedAiStrength_ChangesFingerprint()
    {
        // Tuning the AI signal's Strength re-stamps the fingerprint by value (spec 106) — the deferred Strength
        // recalibration cannot silently produce falsely-comparable snapshots.
        var changed = SourceDescriptor
            + $"ai={DescriptorEscaping.Escape(AiDirectionalDescriptor.Replace("str=8", "str=9", StringComparison.Ordinal))};";

        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(sourceDescriptor: changed));
    }

    [Fact]
    public void Compute_ChangedAiModel_ChangesFingerprint()
    {
        // Spec 119: the earnings-read MODEL is folded in by value because it changes signal DIRECTION (the
        // 2026-07-21 A/B: llama3.1 read EOSE Improving 0.90 where DeepSeek-V4-Flash read the same release
        // Mixed 0.85). Two runs on different models must therefore never share a ScoringConfigVersion —
        // otherwise the efficacy line would be drawn as continuous across a real change.
        const string previousModel = "directional-filing:str=8;nov=6;minconf=0.6;model=ollama:llama3.1";

        Assert.NotEqual(
            DefaultFingerprint(sourceDescriptor: AiOnSourceDescriptor),
            DefaultFingerprint(
                sourceDescriptor: SourceDescriptor + $"ai={DescriptorEscaping.Escape(previousModel)};"));
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(formulaVersion: "radar-formula-v7"));
    }

    [Fact]
    public void Compute_ChangedFollowingTierDiscount_ChangesFingerprint()
    {
        // The spec-117 following-discount magnitudes are hashed by value: tuning a tier discount (a config
        // edit, no formula bump) must re-stamp the fingerprint so runs stay comparable (AD-10).
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { FollowingTierDiscountMega = 0.6 }));
    }

    [Fact]
    public void Compute_ChangedMediaCollapseWindow_ChangesFingerprint()
    {
        // Changing the same-event media-collapse window changes how many MediaAttention signals feed the
        // formula, so the fingerprint must re-stamp automatically by value (spec 109 — no _formula.Version /
        // RuleSetVersion bump; the window magnitude is hashed via the media-collapse descriptor).
        var changedWindow =
            new MediaAttentionCollapse(new MediaCollapseOptions { EventWindowDays = 7.0 }).CanonicalDescriptor();

        Assert.NotEqual(DefaultFingerprint(), DefaultFingerprint(mediaCollapseDescriptor: changedWindow));
    }

    [Fact]
    public void Compute_ChangedCollapsedBreadthCredit_ChangesFingerprint()
    {
        // The spec-122 breadth-preserving-collapse credit is hashed by value: dialling it (a config edit, no
        // formula bump) changes the Attention reach, so runs on different credits must never be falsely
        // comparable (AD-10). Credit 0.0 is the radar-formula-v7-equivalent setting.
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { CollapsedBreadthCredit = 0.0 }));
    }

    // ---- spec 148: the two inputs that used to be hashed into nothing -----------------------------------

    [Fact]
    public void Compute_ChangedScoringWindow_ChangesFingerprint()
    {
        // THE spec-148 acceptance criterion. Radar:ScoringWindowDays selects ScoringOptions.Window, which
        // bounds BOTH the current window and the previous/velocity window — so a 14-day and a 30-day run over
        // the same evidence produce materially different Trajectory, SignalVelocity and Attention. Before this
        // slice they stamped the SAME ScoringConfigVersion, which is exactly the "silently continue one series
        // while measuring something else" failure StrategyIdentityGuard's own error message describes (and,
        // being an in-place edit to a named strategy, the one category the guard structurally could not see).
        Assert.NotEqual(
            DefaultFingerprint(window: TimeSpan.FromDays(30)),
            DefaultFingerprint(window: TimeSpan.FromDays(14)));
    }

    [Fact]
    public void Compute_WindowEncoding_IsInjective_NotTruncatedToWholeDays()
    {
        // The window is hashed as TICKS, deliberately. Whole-days would be lossy: these two windows differ by
        // 12 hours and would collide under a day-truncating encoding, silently making two different scorings
        // share one stamp — the precise failure the field exists to prevent (AD-3 determinism does not permit
        // a lossy identity).
        Assert.NotEqual(
            DefaultFingerprint(window: TimeSpan.FromHours(24)),
            DefaultFingerprint(window: TimeSpan.FromHours(36)));

        // …and sub-day precision survives all the way down to a single tick.
        Assert.NotEqual(
            DefaultFingerprint(window: TimeSpan.FromDays(30)),
            DefaultFingerprint(window: TimeSpan.FromDays(30) + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Compute_ChangedTrajectoryCorroborationK_ChangesFingerprint()
    {
        // THE other spec-148 acceptance criterion, and the last ScoringWeights field the fold had missed
        // (recorded as a known gap by the spec-146 hand-back). k is the denominator smoother in
        // radar-formula-v8's T_raw = 10·(Mpos−Mneg)/(Mpos+Mneg+k) AND — since spec 146 — in v9's per-channel
        // direction factor, so tuning it moves scores in both formulas.
        Assert.NotEqual(
            DefaultFingerprint(),
            DefaultFingerprint(weights: new ScoringWeights { TrajectoryCorroborationK = 4.0 }));
    }

    [Fact]
    public void Compute_EveryScoringWeightsProperty_IsFoldedIntoTheFingerprint()
    {
        // A COMPLETENESS GUARD, not a spot check. Spec 148 exists because TrajectoryCorroborationK sat
        // unfolded for seven slices while every review read the fold as exhaustive. Enumerating the record's
        // public properties by reflection makes the NEXT unfolded weight fail loudly here — the day it is
        // added — instead of silently producing falsely-comparable snapshots.
        //
        // Perturbation only has to change the hashed string: Compute() does not call Validate(), so a value
        // that would be rejected at startup is still a legitimate probe of whether the field is read at all.
        var baseline = DefaultFingerprint();
        var properties = typeof(ScoringWeights)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToList();

        // NO CanWrite FILTER. Filtering on settability would silently EXCLUDE a future
        // `public double Foo { get; }` from the very guard that exists to catch the next unfolded weight —
        // exhaustiveness is the whole point, so an unperturbable property must fail here and force a
        // conscious decision rather than disappear from the enumeration.
        Assert.All(
            properties,
            p => Assert.True(
                p.CanRead && p.CanWrite,
                $"ScoringWeights.{p.Name} is not both readable and settable, so this completeness guard "
                    + "cannot perturb it. Give it an init accessor, or fold it in and extend this test "
                    + "deliberately — do not let it fall out of the enumeration."));

        // Not vacuous: if the reflection query ever returns nothing (a refactor to fields, say) the loop
        // would pass by doing nothing at all.
        Assert.Equal(27, properties.Count);

        foreach (var property in properties)
        {
            Assert.Equal(typeof(double), property.PropertyType);

            var perturbed = new ScoringWeights();
            var current = (double)property.GetValue(perturbed)!;
            // A distinct, finite sentinel for every field regardless of its default (0.0 included).
            property.SetValue(perturbed, current + 0.123456789);

            Assert.NotEqual(baseline, DefaultFingerprint(weights: perturbed));
        }
    }

    [Fact]
    public void ScoringOptions_ExposesExactlyOneKnob_AndItIsFolded()
    {
        // The ScoringOptions equivalent of the ScoringWeights completeness guard above. ScoringOptions is a
        // plain class rather than a record of doubles, so instead of perturbing every property generically
        // this pins the SET of properties: exactly one, named Window, of type TimeSpan — and the test above
        // proves that one is folded. A second operational knob therefore cannot be added without a conscious
        // decision about whether it is output-affecting, which is precisely the decision that was skipped for
        // Window itself between spec 89 and spec 148.
        var properties = typeof(ScoringOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, p.PropertyType))
            .ToList();

        Assert.Equal([(nameof(ScoringOptions.Window), typeof(TimeSpan))], properties);
    }
}
