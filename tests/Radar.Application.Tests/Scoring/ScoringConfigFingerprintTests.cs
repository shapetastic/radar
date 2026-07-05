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

    [Fact]
    public void Compute_SameInputs_ProduceSameFingerprint()
    {
        var a = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());
        var b = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexToken_OfStableLength()
    {
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

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
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt any non-invariant number formatting.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var underDeDe = ScoringConfigFingerprint.Compute(
                "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

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
        // Pinned so (a) default runs stay comparable to each other and (b) any accidental default-weight or
        // default-tier drift is caught (the automatic AD-10 replacement for the hand-bumped constant). This value
        // is the automatic AD-10 re-stamp for the spec-94 MediaReachWeight 0.25 → 0.10 recalibration (which
        // de-saturates Attention); it superseded the spec-90 attention-tier re-stamp. MediaReachWeight is part of
        // the hashed canonical string, so the default fingerprint changed automatically — no manual version bump.
        var fp = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

        Assert.Equal("radar-scoring-fp-5cd50423f408", fp);
    }

    [Fact]
    public void Compute_ChangedWeight_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5",
            new ScoringWeights { AttentionHalfSaturation = 12.0 }, DefaultTierDescriptor());

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedTierDescriptor_ChangesFingerprint()
    {
        var baseline = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

        var changed = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), "unknown=0.9;");

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_ChangedFormulaVersion_ChangesFingerprint()
    {
        var v5 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v5", new ScoringWeights(), DefaultTierDescriptor());

        var v4 = ScoringConfigFingerprint.Compute(
            "mvp-engine-v1", "radar-formula-v4", new ScoringWeights(), DefaultTierDescriptor());

        Assert.NotEqual(v5, v4);
    }
}
