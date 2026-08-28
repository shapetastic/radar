namespace Radar.Application.Scoring;

/// <summary>
/// The typed result of resolving a publisher <c>SourceName</c> against the curated attention tier map
/// (spec 196 §3). It exists because spec 196 §1 inverted <c>UnknownWeight</c> to the <c>Mill</c> weight
/// (0.1): from that moment an explicitly-classified <c>Mill</c> publisher and an unrecognised one return
/// the SAME number, so a <see cref="double"/> can no longer tell "we looked at this outlet and decided it
/// is a content mill" from "we have never classified this outlet". Anything that needs that distinction —
/// above all the spec-196 capture-flow coverage diagnostic — must therefore consume the resolver rather
/// than the weight.
/// <para>
/// <b>The invariant is structural, not conventional:</b> <see cref="IsExplicitlyMapped"/> is true iff
/// <see cref="TierName"/> is a real tier name, enforced by the private constructor plus the two factories.
/// An unclassified resolution always carries <see cref="UnclassifiedTierName"/>, which is deliberately not
/// a legal configured tier name shape, so a curated tier can never impersonate the sentinel.
/// </para>
/// <para>
/// <see cref="NormalizedPublisher"/> is the key the lookup actually matched on (or would have matched on),
/// so a curator can see WHY a publisher missed — the usual answer being a name variant that normalizes to
/// a different key than the listed one.
/// </para>
/// </summary>
public sealed record AttentionSourceResolution
{
    /// <summary>
    /// The <see cref="TierName"/> carried by a publisher that is in no configured tier. Parenthesised so it
    /// cannot be confused with a curated tier name in a rendered diagnostic.
    /// </summary>
    public const string UnclassifiedTierName = "(unclassified)";

    private AttentionSourceResolution(
        string tierName, double weight, bool isExplicitlyMapped, string normalizedPublisher)
    {
        TierName = tierName;
        Weight = weight;
        IsExplicitlyMapped = isExplicitlyMapped;
        NormalizedPublisher = normalizedPublisher;
    }

    /// <summary>The matched tier's name, or <see cref="UnclassifiedTierName"/>.</summary>
    public string TierName { get; }

    /// <summary>The attention-breadth weight in [0,1] — the matched tier's weight, or the unknown default.</summary>
    public double Weight { get; }

    /// <summary>
    /// True iff this publisher is explicitly listed in a configured tier. The bit that survives two tiers
    /// (or a tier and the unknown default) sharing one weight.
    /// </summary>
    public bool IsExplicitlyMapped { get; }

    /// <summary>
    /// The normalized lookup key derived from the supplied name (empty for a blank/null name). Diagnostic
    /// provenance: it is what the map was probed with.
    /// </summary>
    public string NormalizedPublisher { get; }

    /// <summary>A publisher explicitly listed in <paramref name="tierName"/>.</summary>
    public static AttentionSourceResolution Mapped(
        string tierName, double weight, string normalizedPublisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierName);
        ArgumentNullException.ThrowIfNull(normalizedPublisher);

        return new AttentionSourceResolution(tierName, weight, true, normalizedPublisher);
    }

    /// <summary>
    /// A publisher in no configured tier, carrying the configured unknown default. Since spec 196 that
    /// default is the <c>Mill</c> weight, which is exactly why the caller must read
    /// <see cref="IsExplicitlyMapped"/> rather than compare weights.
    /// </summary>
    public static AttentionSourceResolution Unclassified(double weight, string normalizedPublisher)
    {
        ArgumentNullException.ThrowIfNull(normalizedPublisher);

        return new AttentionSourceResolution(UnclassifiedTierName, weight, false, normalizedPublisher);
    }
}
