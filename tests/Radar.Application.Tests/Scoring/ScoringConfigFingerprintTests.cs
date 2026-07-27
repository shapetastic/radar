using System.Globalization;
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

    [Fact]
    public void Compute_SameInputs_ProduceSameFingerprint()
    {
        var a = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);
        var b = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        const string prefix = "radar-scoring-fp-";
        Assert.StartsWith(prefix, fp, StringComparison.Ordinal);

        var hex = fp[prefix.Length..];
        Assert.Equal(12, hex.Length);
        Assert.All(hex, ch => Assert.True(Uri.IsHexDigit(ch) && !char.IsUpper(ch), $"'{ch}' must be lowercase hex"));
    }

    [Fact]
    public void Compute_IsCultureInvariant()
    {
        var invariant = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var underDeDe = ScoringConfigFingerprint.Compute(
                "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
                InsiderDescriptor, MediaCollapseDescriptor);

            Assert.Equal(invariant, underDeDe);
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
        // unnoticed default-weight, default-tier, rule-set, insider-materiality or media-collapse drift fails
        // here, and the author must then decide whether the move was intended and record its lineage.
        //
        // What the pin no longer does is pretend the fingerprint never changes. It has in fact changed 17
        // times over 851 live snapshots (11 radar-scoring-fp-* + 6 legacy radar-scoring-config-vN); the
        // largest cohort is 133 snapshots ≈ 3 runs, and the pinned AI-ON value below had exactly 43 — one
        // single run. The score series is keyed by StrategyName now (ScoreSeriesKey), so a pin move no longer
        // fragments anything; it re-stamps recorded provenance and trips StrategyIdentityGuard, which is
        // exactly what it should do.
        //
        // Lineage: spec 133 (radar-scoring-fp-6b2f468041b9 — the 7-collector default) → SPEC 141, which
        // removes the enabled-collector CSV from the hashed identity altogether. THE MOVE IS THE DELIVERABLE.
        // Scoring math is byte-identical (asserted separately by the engine tests); only the stamp differs.
        //
        // SPEC 146 DELIBERATELY DID NOT MOVE THIS PIN. It added a per-strategy Formula and a channel budget,
        // and it extracted v8's per-signal primitives into the shared ScoreSignalMath — but the default
        // strategy still names radar-formula-v8, still declares no channels (ScoringChannelSet.Empty folds in
        // as a verbatim passthrough), and the extraction preserved v8's expression shapes and accumulation
        // order, so both the hashed inputs and the scores are unchanged.
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal("radar-scoring-fp-2ce20f8fc497", fp);
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8",
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), "unknown=0.9;", SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
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
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(),
            "rules=radar-keyword-rules-v7;",
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedInsiderTiers_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        // Changing an insider tier (or the cluster boost) changes the effective scoring config, so the
        // fingerprint must re-stamp automatically (spec 96 — magnitudes hashed by value, no RuleSetVersion bump).
        var changedInsider = new InsiderMaterialityWeights { ClusterBoost = 2 }.CanonicalDescriptor();
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            changedInsider, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
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
        // an AI-on and an AI-off run (the AI analogue of spec 95's secform4 fix). The AI-OFF pin above is unmoved.
        var aiOff = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var aiOn = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(aiOff, aiOn);
    }

    [Fact]
    public void Compute_AiOnDefault_MatchesPinnedFingerprint()
    {
        // The live AI-ON default fingerprint the scripts/run-profiles/default.json run produces: with an AI
        // provider registered, the AI directional-filing descriptor is folded in (AiOnSourceDescriptor above), so
        // the effective config differs from the AI-OFF pin. Pinned so an accidental drift in the AI directional
        // magnitudes, the earnings-read model, or any other folded input is caught for the AI-ON run too.
        //
        // A CHANGE-DETECTOR, NOT AN INVARIANT — see the AI-OFF pin above for why a deliberate move is normal.
        // This particular value is the sharpest evidence for that position: it had exactly 43 snapshots on the
        // live store, i.e. ONE RUN of history.
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
        // automatically (radar-scoring-fp-57356123e09b) → SPEC 141, which removes the collector CSV from the
        // hashed identity entirely, so a re-stamp like spec 133's can never happen again. THE MOVE IS THE
        // DELIVERABLE; scoring math is byte-identical. → SPEC 146 deliberately did NOT move it: see the
        // AI-OFF pin above for why the per-strategy Formula/Channels addition folds in here as a no-op.
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal("radar-scoring-fp-3457da53489d", fp);
    }

    [Fact]
    public void Compute_ChangedAiStrength_ChangesFingerprint()
    {
        // Tuning the AI signal's Strength re-stamps the fingerprint by value (spec 106) — the deferred Strength
        // recalibration cannot silently produce falsely-comparable snapshots.
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(),
            SourceDescriptor
                + $"ai={DescriptorEscaping.Escape(AiDirectionalDescriptor.Replace("str=8", "str=9", StringComparison.Ordinal))};",
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedAiModel_ChangesFingerprint()
    {
        // Spec 119: the earnings-read MODEL is folded in by value because it changes signal DIRECTION (the
        // 2026-07-21 A/B: llama3.1 read EOSE Improving 0.90 where DeepSeek-V4-Flash read the same release
        // Mixed 0.85). Two runs on different models must therefore never share a ScoringConfigVersion —
        // otherwise the efficacy line would be drawn as continuous across a real change.
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var previousModel =
            "directional-filing:str=8;nov=6;minconf=0.6;model=ollama:llama3.1";
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(),
            SourceDescriptor + $"ai={DescriptorEscaping.Escape(previousModel)};",
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        var v8 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var v7 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(v8, v7);
    }

    [Fact]
    public void Compute_ChangedFollowingTierDiscount_ChangesFingerprint()
    {
        // The spec-117 following-discount magnitudes are hashed by value: tuning a tier discount (a config
        // edit, no formula bump) must re-stamp the fingerprint so runs stay comparable (AD-10).
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8",
            new ScoringWeights { FollowingTierDiscountMega = 0.6 }, DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedMediaCollapseWindow_ChangesFingerprint()
    {
        // Changing the same-event media-collapse window changes how many MediaAttention signals feed the
        // formula, so the fingerprint must re-stamp automatically by value (spec 109 — no _formula.Version /
        // RuleSetVersion bump; the window magnitude is hashed via the media-collapse descriptor).
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changedWindow =
            new MediaAttentionCollapse(new MediaCollapseOptions { EventWindowDays = 7.0 }).CanonicalDescriptor();
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, changedWindow);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedCollapsedBreadthCredit_ChangesFingerprint()
    {
        // The spec-122 breadth-preserving-collapse credit is hashed by value: dialling it (a config edit, no
        // formula bump) changes the Attention reach, so runs on different credits must never be falsely
        // comparable (AD-10). Credit 0.0 is the radar-formula-v7-equivalent setting.
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v8",
            new ScoringWeights { CollapsedBreadthCredit = 0.0 }, DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }
}
