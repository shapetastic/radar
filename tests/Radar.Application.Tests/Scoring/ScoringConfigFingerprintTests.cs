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
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);
        var b = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
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
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var underDeDe = ScoringConfigFingerprint.Compute(
                "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
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
        // AD-10 replacement for the hand-bumped constant). This value is the spec-110 re-stamp: the default
        // insider SellTiers were recalibrated to the materiality-scaled asymmetric curve (buy>>sell), which is
        // hashed into the fingerprint by value via the insider descriptor (spec 96), so the default fingerprint
        // re-stamped automatically — scoring math on non-insider-sell signals is unchanged and no
        // _formula.Version / RuleSetVersion bump occurred. It supersedes the spec-109 stamp
        // (radar-scoring-fp-525e552874eb) and matches default.json's recorded live default.
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.Equal("radar-scoring-fp-abbdf9fab44f", fp);
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5",
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), "unknown=0.9;", SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedSignalSourceDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        // Dropping a collector from the enabled set changes the signal-production surface, so the fingerprint
        // must re-stamp (spec 95 — restores the spec-69 comparability guarantee across a collector transition).
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(),
            "rules=radar-keyword-rules-v3;collectors=RssPressReleaseCollector,newssearch,sec-edgar,usaspending;",
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedInsiderTiers_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        // Changing an insider tier (or the cluster boost) changes the effective scoring config, so the
        // fingerprint must re-stamp automatically (spec 96 — magnitudes hashed by value, no RuleSetVersion bump).
        var changedInsider = new InsiderMaterialityWeights { ClusterBoost = 2 }.CanonicalDescriptor();
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            changedInsider, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    // The AI-ON signal-source descriptor (spec 106): the same AI-OFF SourceDescriptor with the directional-filing
    // source's per-signal magnitudes appended as an escaped ai=… segment (the default Strength/Novelty/MinConfidence
    // == 6/6/0.6). This is what SignalSourceDescriptor produces when the opt-in AI path is registered.
    private const string AiOnSourceDescriptor =
        SourceDescriptor + "ai=directional-filing:str=6;nov=6;minconf=0.6;";

    [Fact]
    public void Compute_AiOnSourceDescriptor_DiffersFromAiOff()
    {
        // Enabling the AI directional-filing path widens the signal-production surface (it emits directional
        // GuidanceChange signals), so the fingerprint MUST re-stamp — closing the AD-10 comparability gap between
        // an AI-on and an AI-off run (the AI analogue of spec 95's secform4 fix). The AI-OFF pin above is unmoved.
        var aiOff = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var aiOn = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(aiOff, aiOn);
    }

    [Fact]
    public void Compute_ChangedAiStrength_ChangesFingerprint()
    {
        // Tuning the AI signal's Strength re-stamps the fingerprint by value (spec 106) — the deferred Strength
        // recalibration cannot silently produce falsely-comparable snapshots.
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), AiOnSourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(),
            SourceDescriptor + "ai=directional-filing:str=9;nov=6;minconf=0.6;",
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        var v5 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var v4 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v4", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        Assert.NotEqual(v5, v4);
    }

    [Fact]
    public void Compute_ChangedMediaCollapseWindow_ChangesFingerprint()
    {
        // Changing the same-event media-collapse window changes how many MediaAttention signals feed the
        // formula, so the fingerprint must re-stamp automatically by value (spec 109 — no _formula.Version /
        // RuleSetVersion bump; the window magnitude is hashed via the media-collapse descriptor).
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, MediaCollapseDescriptor);

        var changedWindow =
            new MediaAttentionCollapse(new MediaCollapseOptions { EventWindowDays = 7.0 }).CanonicalDescriptor();
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor, changedWindow);

        Assert.NotEqual(baseline, changed);
    }
}
