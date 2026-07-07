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

    [Fact]
    public void Compute_SameInputs_ProduceSameFingerprint()
    {
        var a = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);
        var b = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

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
            InsiderDescriptor);

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var underDeDe = ScoringConfigFingerprint.Compute(
                "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
                InsiderDescriptor);

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
        // default-tier, signal-source, or insider-materiality drift is caught (the automatic AD-10 replacement
        // for the hand-bumped constant). This value is the spec-103 re-stamp: the RuleSetVersion bump
        // radar-keyword-rules-v2 → v3 (the new HiringActivity rule group) moves the signal-source descriptor,
        // so the default fingerprint re-stamped automatically — the enabled 6-collector set is UNCHANGED
        // (hiringats is opt-in OFF) and scoring math is byte-identical. It supersedes the spec-100
        // 6-collector stamp (radar-scoring-fp-8d638b90d4aa) and matches default.json's recorded live default.
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        Assert.Equal("radar-scoring-fp-c9e609ed53e9", fp);
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5",
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), "unknown=0.9;", SourceDescriptor,
            InsiderDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedSignalSourceDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        // Dropping a collector from the enabled set changes the signal-production surface, so the fingerprint
        // must re-stamp (spec 95 — restores the spec-69 comparability guarantee across a collector transition).
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(),
            "rules=radar-keyword-rules-v3;collectors=RssPressReleaseCollector,newssearch,sec-edgar,usaspending;",
            InsiderDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedInsiderTiers_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        // Changing an insider tier (or the cluster boost) changes the effective scoring config, so the
        // fingerprint must re-stamp automatically (spec 96 — magnitudes hashed by value, no RuleSetVersion bump).
        var changedInsider = new InsiderMaterialityWeights { ClusterBoost = 2 }.CanonicalDescriptor();
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            changedInsider);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        var v5 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        var v4 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v4", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor,
            InsiderDescriptor);

        Assert.NotEqual(v5, v4);
    }
}
