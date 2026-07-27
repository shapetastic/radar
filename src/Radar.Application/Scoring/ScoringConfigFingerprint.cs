using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Radar.Application.Scoring;

/// <summary>
/// Computes a deterministic content fingerprint of the effective resolved scoring config — the structure
/// identity (engine + formula version) plus every <see cref="ScoringWeights"/> value plus the attention
/// tier-map descriptor plus the signal-source IDENTITY descriptor (the extractor rule-set identity, the
/// optional AI directional-filing magnitudes and the strategy's declared signal types) plus the
/// insider-materiality descriptor (the config-tunable buy/sell tiers + cluster boost, spec 96) plus the
/// media-collapse descriptor (the same-event media-attention collapse structure + window, spec 109) plus the
/// recent-signal WINDOW LENGTH (<see cref="ScoringOptions.Window"/>, spec 148) — so a
/// snapshot's <c>ScoringConfigVersion</c> uniquely identifies the STRATEGY that produced it (AD-10 as
/// amended). The canonical string uses a FIXED, explicit field ordering (never reflection order, which is
/// unstable across runtimes) and culture-invariant round-trip number formatting (AD-3), then hashes with the
/// shared EvidenceNormalizer idiom (<c>Convert.ToHexStringLower(SHA256.HashData(...))</c>). Any
/// output-affecting change (formula shape, any weight, the tier map, the window length) changes the
/// fingerprint automatically. Pure and deterministic — no clock, IO, or randomness.
/// <para>
/// Spec 141 narrowed the VALUE of the <c>srcDesc</c> field, not this signature: the enabled-collector set is
/// no longer part of the signal-source descriptor handed in here (it is recorded on the snapshot as
/// <c>CollectionProvenance</c> and hashed into nothing), so a collector toggle no longer re-stamps a
/// strategy. The field key and ordering are unchanged; the pinned default fingerprints moved once,
/// deliberately, in that slice.
/// </para>
/// <para>
/// SPEC 148 CLOSED THE LAST TWO HOLES. Two genuinely output-affecting inputs were hashed into nothing: the
/// scoring <c>window</c> (a 14-day and a 30-day run produce materially different Trajectory, SignalVelocity
/// and Attention, yet stamped the same value) and <see cref="ScoringWeights.TrajectoryCorroborationK"/> (the
/// v8 Trajectory denominator, and since spec 146 the v9 channel direction factor's denominator too). Both are
/// folded now; the pinned defaults moved once, deliberately, in that slice. The <c>window</c> is the LAST
/// field, appended after <c>mediaCollapse</c>, following the fixed-position pattern specs 96/109 used.
/// </para>
/// </summary>
public static class ScoringConfigFingerprint
{
    /// <summary>
    /// Computes the fingerprint token for the given effective scoring config. The returned value is a
    /// stable single opaque token, human-glanceable via a short prefix: <c>radar-scoring-fp-&lt;12 hex&gt;</c>.
    /// </summary>
    /// <param name="window">
    /// The recent-signal window length (<see cref="ScoringOptions.Window"/>, bound from
    /// <c>Radar:ScoringWindowDays</c>). Encoded as <see cref="TimeSpan.Ticks"/> — a <c>long</c> formatted
    /// invariant-culture — because ticks is INJECTIVE over every <see cref="TimeSpan"/> (AD-3): whole-days
    /// is not, so a 36-hour and a 24-hour window would truncate onto the same field value and two genuinely
    /// different scorings would share one stamp. That is precisely the failure this field exists to prevent,
    /// so the encoding must not be lossy.
    /// </param>
    public static string Compute(
        string engineVersion,
        string formulaVersion,
        ScoringWeights weights,
        string attentionDescriptor,
        string signalSourceDescriptor,
        string insiderMaterialityDescriptor,
        string mediaCollapseDescriptor,
        TimeSpan window)
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
        // Spec 148: the corroboration-smoothing constant k, folded in HERE — immediately after
        // TrajectoryScale, matching ScoringWeights' own declaration order. It is output-affecting TWICE over:
        // it is the denominator smoother in radar-formula-v8's T_raw = 10·(Mpos−Mneg)/(Mpos+Mneg+k), and
        // since spec 146 it is also the denominator of radar-formula-v9's per-channel direction factor. It
        // was the only ScoringWeights field the fold had ever missed, so tuning it silently produced
        // falsely-comparable snapshots.
        Append(builder, nameof(weights.TrajectoryCorroborationK), weights.TrajectoryCorroborationK);
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
        // Spec 148: the recent-signal window, LAST, as ticks (see the <param> note for why ticks and not
        // days). The window decides which signals a snapshot is computed over — both the current window and
        // the previous/velocity window — so two runs at different window lengths are different scorings.
        Append(builder, "window", window.Ticks);

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

    /// <summary>
    /// The integral counterpart of the <c>double</c> overload, mirroring its culture-invariant formatting so
    /// no field can ever pick up a locale-specific group separator (AD-3).
    /// </summary>
    private static void Append(StringBuilder builder, string key, long value)
    {
        builder.Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
    }
}
