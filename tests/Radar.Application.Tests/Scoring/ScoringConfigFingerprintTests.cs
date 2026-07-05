using System.Globalization;
using Radar.Application.Scoring;
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
    // sec→"sec-edgar", secform4→"sec-form4", usaspending→"usaspending", newssearch→"newssearch"),
    // Ordinal-sorted — NOT the Radar:Collectors config "kinds" — so it matches what the Worker actually produces.
    private const string SourceDescriptor =
        "rules=radar-keyword-rules-v1;collectors=RssPressReleaseCollector,newssearch,sec-edgar,sec-form4,usaspending;";

    [Fact]
    public void Compute_SameInputs_ProduceSameFingerprint()
    {
        var a = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);
        var b = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

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
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var underDeDe = ScoringConfigFingerprint.Compute(
                "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

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
        // default-tier, or signal-source drift is caught (the automatic AD-10 replacement for the hand-bumped
        // constant). This value is the spec-95 re-stamp: the signal-source descriptor (the enabled collector
        // set + extractor rule-set identity, KeywordSignalExtractor.RuleSetVersion) is now part of the hashed
        // canonical string, appended after the attention descriptor, so the default fingerprint changed
        // automatically — no manual version bump. It supersedes the spec-94 MediaReachWeight re-stamp
        // (radar-scoring-fp-5cd50423f408).
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        Assert.Equal("radar-scoring-fp-55270b9d8fad", fp);
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5",
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, DefaultTierDescriptor(), SourceDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), "unknown=0.9;", SourceDescriptor);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedSignalSourceDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        // Dropping a collector from the enabled set changes the signal-production surface, so the fingerprint
        // must re-stamp (spec 95 — restores the spec-69 comparability guarantee across a collector transition).
        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(),
            "rules=radar-keyword-rules-v1;collectors=RssPressReleaseCollector,newssearch,sec-edgar,usaspending;");

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        var v5 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        var v4 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v4", new ScoringWeights(), DefaultTierDescriptor(), SourceDescriptor);

        Assert.NotEqual(v5, v4);
    }
}
