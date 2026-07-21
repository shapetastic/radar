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

    // The signal-source descriptor of the default run profile (spec 95): the enabled collector set + the
    // extractor rule-set identity, canonicalized. It is folded into the fingerprint after the attention
    // descriptor, so the default fingerprint value depends on it. The collector tokens are the concrete
    // IEvidenceCollector.CollectorName values the default DI graph registers (rss→"RssPressReleaseCollector",
    // sec→"sec-edgar", secform4→"sec-form4", sec13dg→"sec-13dg", usaspending→"usaspending",
    // newssearch→"newssearch"), Ordinal-sorted — NOT the Radar:Collectors config "kinds" — so it matches what
    // the Worker actually produces. This is the 6-collector default after spec 100 promoted sec13dg into
    // scripts/run-profiles/default.json (commit 58c55f5); spec 103 bumps the rule-set identity to
    // radar-keyword-rules-v3 (the new HiringActivity group) while the enabled collector CSV stays this same
    // 6-collector set (hiringats is opt-in OFF). Ordinal sort places "sec-13dg" before "sec-edgar"
    // (the char after "sec-" is '1' 0x31 < 'e' 0x65).
    private const string SourceDescriptor =
        "rules=radar-keyword-rules-v3;collectors=RssPressReleaseCollector,newssearch,sec-13dg,sec-edgar,sec-form4,usaspending;";

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
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);
        var b = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
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
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var underDeDe = ScoringConfigFingerprint.Compute(
                "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
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
        // Pinned so (a) default runs stay comparable to each other and (b) any accidental default-weight,
        // default-tier, signal-source, insider-materiality, or media-collapse drift is caught (the automatic
        // AD-10 replacement for the hand-bumped constant). This value is the spec-117 re-stamp: the
        // Opportunity discount became following-tier-aware (radar-formula-v7 — a STRUCTURE change), so
        // _formula.Version advanced v6→v7 AND the seven new following-discount ScoringWeights magnitudes
        // were folded into the canonical string, re-stamping the fingerprint automatically. It supersedes
        // the spec-111 stamp (radar-scoring-fp-c45fb79092ea) and matches default.json's recorded default.
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal("radar-scoring-fp-8f4b59efd288", fp);
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7",
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), "unknown=0.9;", SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedSignalSourceDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        // Dropping a collector from the enabled set changes the signal-production surface, so the fingerprint
        // must re-stamp (spec 95 — restores the spec-69 comparability guarantee across a collector transition).
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(),
            "rules=radar-keyword-rules-v3;collectors=RssPressReleaseCollector,newssearch,sec-edgar,usaspending;",
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedInsiderTiers_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        // Changing an insider tier (or the cluster boost) changes the effective scoring config, so the
        // fingerprint must re-stamp automatically (spec 96 — magnitudes hashed by value, no RuleSetVersion bump).
        var changedInsider = new InsiderMaterialityWeights { ClusterBoost = 2 }.CanonicalDescriptor();
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
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
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var aiOn = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
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
        // Lineage: spec 112 (radar-scoring-fp-454984785732) → spec 117 radar-formula-v7 structure bump +
        // following-discount weights (radar-scoring-fp-4c06fd2d2d8c) → spec 119, which (a) folds the earnings-read
        // model identity into the directional descriptor by value (the default switches from ollama/llama3.1 to
        // the DeepInfra openai provider on deepseek-ai/DeepSeek-V4-Flash — a DIRECTION-changing input, so runs
        // must not be falsely comparable) and (b) builds the ai= segment through the real escaping so the pin is
        // what a live run actually stamps. No _formula.Version / RuleSetVersion bump; the AI-OFF pin above is
        // deliberately UNMOVED (an AI-OFF run has no directional path, so nothing is appended).
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal("radar-scoring-fp-2ef5ef96cce2", fp);
    }

    [Fact]
    public void Compute_ChangedAiStrength_ChangesFingerprint()
    {
        // Tuning the AI signal's Strength re-stamps the fingerprint by value (spec 106) — the deferred Strength
        // recalibration cannot silently produce falsely-comparable snapshots.
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(),
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
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var previousModel =
            "directional-filing:str=8;nov=6;minconf=0.6;model=ollama:llama3.1";
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(),
            SourceDescriptor + $"ai={DescriptorEscaping.Escape(previousModel)};",
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        var v7 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var v6 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v6", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(v7, v6);
    }

    [Fact]
    public void Compute_ChangedFollowingTierDiscount_ChangesFingerprint()
    {
        // The spec-117 following-discount magnitudes are hashed by value: tuning a tier discount (a config
        // edit, no formula bump) must re-stamp the fingerprint so runs stay comparable (AD-10).
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7",
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
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changedWindow =
            new MediaAttentionCollapse(new MediaCollapseOptions { EventWindowDays = 7.0 }).CanonicalDescriptor();
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v7", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, changedWindow);

        Assert.NotEqual(baseline, changed);
    }
}
