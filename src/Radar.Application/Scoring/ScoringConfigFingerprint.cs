using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Radar.Application.Scoring;

/// <summary>
/// Computes a deterministic content fingerprint of the effective resolved scoring config — the structure
/// identity (engine + formula version) plus every <see cref="ScoringWeights"/> value plus the attention
/// tier-map descriptor plus the signal-source descriptor (the enabled collector set + extractor rule-set
/// identity) plus the insider-materiality descriptor (the config-tunable buy/sell tiers + cluster boost,
/// spec 96) plus the media-collapse descriptor (the same-event media-attention collapse structure + window,
/// spec 109) — so a snapshot's <c>ScoringConfigVersion</c> uniquely identifies the generation
/// that produced it (AD-10 as amended). The canonical string uses a FIXED, explicit field ordering (never
/// reflection order, which is unstable across runtimes) and culture-invariant round-trip number formatting
/// (AD-3), then hashes with the shared EvidenceNormalizer idiom
/// (<c>Convert.ToHexStringLower(SHA256.HashData(...))</c>). Any output-affecting change (formula shape, any
/// weight, the tier map) changes the fingerprint automatically, so the AD-10 comparability property can no
/// longer be silently forgotten. Pure and deterministic — no clock, IO, or randomness.
/// </summary>
public static class ScoringConfigFingerprint
{
    /// <summary>
    /// Computes the fingerprint token for the given effective scoring config. The returned value is a
    /// stable single opaque token, human-glanceable via a short prefix: <c>radar-scoring-fp-&lt;12 hex&gt;</c>.
    /// </summary>
    public static string Compute(
        string engineVersion,
        string formulaVersion,
        ScoringWeights weights,
        string attentionDescriptor,
        string signalSourceDescriptor,
        string insiderMaterialityDescriptor,
        string mediaCollapseDescriptor)
    {
        ArgumentNullException.ThrowIfNull(engineVersion);
        ArgumentNullException.ThrowIfNull(formulaVersion);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(attentionDescriptor);
        ArgumentNullException.ThrowIfNull(signalSourceDescriptor);
        ArgumentNullException.ThrowIfNull(insiderMaterialityDescriptor);
        ArgumentNullException.ThrowIfNull(mediaCollapseDescriptor);

        var builder = new StringBuilder();
        Append(builder, "engine", engineVersion);
        Append(builder, "formula", formulaVersion);
        Append(builder, nameof(weights.RecencyFloor), weights.RecencyFloor);
        Append(builder, nameof(weights.TrajectoryNeutral), weights.TrajectoryNeutral);
        Append(builder, nameof(weights.TrajectoryScale), weights.TrajectoryScale);
        Append(builder, nameof(weights.AttentionHalfSaturation), weights.AttentionHalfSaturation);
        Append(builder, nameof(weights.MediaReachWeight), weights.MediaReachWeight);
        Append(builder, nameof(weights.QualityPrimarySource), weights.QualityPrimarySource);
        Append(builder, nameof(weights.QualityHigh), weights.QualityHigh);
        Append(builder, nameof(weights.QualityMedium), weights.QualityMedium);
        Append(builder, nameof(weights.QualityLow), weights.QualityLow);
        Append(builder, nameof(weights.QualityUnknown), weights.QualityUnknown);
        Append(builder, nameof(weights.EcQualityBase), weights.EcQualityBase);
        Append(builder, nameof(weights.EcQualitySpan), weights.EcQualitySpan);
        Append(builder, nameof(weights.EcDiversityBase), weights.EcDiversityBase);
        Append(builder, nameof(weights.EcDiversitySpan), weights.EcDiversitySpan);
        Append(builder, nameof(weights.DiversityTarget), weights.DiversityTarget);
        Append(builder, nameof(weights.VelocitySmoothing), weights.VelocitySmoothing);
        Append(builder, nameof(weights.VelocitySteady), weights.VelocitySteady);
        Append(builder, nameof(weights.OpportunityAttentionDivisor), weights.OpportunityAttentionDivisor);
        // radar-formula-v7 following-discount magnitudes (spec 117), appended AFTER the divisor in this
        // fixed order — changing any of them (e.g. a tier discount) re-stamps the fingerprint by value.
        Append(builder, nameof(weights.OpportunityAttentionDiscountWeight), weights.OpportunityAttentionDiscountWeight);
        Append(builder, nameof(weights.FollowingTierDiscountMega), weights.FollowingTierDiscountMega);
        Append(builder, nameof(weights.FollowingTierDiscountLarge), weights.FollowingTierDiscountLarge);
        Append(builder, nameof(weights.FollowingTierDiscountMid), weights.FollowingTierDiscountMid);
        Append(builder, nameof(weights.FollowingTierDiscountSmall), weights.FollowingTierDiscountSmall);
        Append(builder, nameof(weights.FollowingTierDiscountWeight), weights.FollowingTierDiscountWeight);
        Append(builder, nameof(weights.OpportunityDiscountFloor), weights.OpportunityDiscountFloor);
        // radar-formula-v8 breadth-preserving-collapse credit (spec 122), appended AFTER the last v7
        // discount weight in this fixed order — tuning it changes the Attention reach, so it re-stamps the
        // fingerprint by value.
        Append(builder, nameof(weights.CollapsedBreadthCredit), weights.CollapsedBreadthCredit);
        Append(builder, "attnDesc", attentionDescriptor);
        Append(builder, "srcDesc", signalSourceDescriptor);
        Append(builder, "insiderDesc", insiderMaterialityDescriptor);
        Append(builder, "mediaCollapse", mediaCollapseDescriptor);

        var canonical = builder.ToString();
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return $"radar-scoring-fp-{hex[..12]}";
    }

    private static void Append(StringBuilder builder, string key, string value)
    {
        builder.Append(key).Append('=').Append(value).Append(';');
    }

    private static void Append(StringBuilder builder, string key, double value)
    {
        builder.Append(key).Append('=').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
    }
}
