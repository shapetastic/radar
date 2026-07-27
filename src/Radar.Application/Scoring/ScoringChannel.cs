using System.Collections.ObjectModel;

namespace Radar.Application.Scoring;

/// <summary>
/// What a <see cref="ScoringChannel"/> measures. The kind decides the sub-score SHAPE, so it is a closed
/// enum in code (a new kind is a formula-structure change, AD-6) while a channel's weight and saturation
/// stay tunable magnitudes in config (spec 89 / AD-10).
/// </summary>
public enum ScoringChannelKind
{
    /// <summary>
    /// A channel over the signals whose EVIDENCE was retrieved by one of the channel's declared collectors
    /// (<see cref="ScoringChannel.Collectors"/>). Its sub-score is a saturating activity term shaped by the
    /// directional preponderance of those signals.
    /// </summary>
    Collector = 0,

    /// <summary>
    /// The cross-source distinct-publisher BREADTH channel. Attention is inherently cross-source — it cannot
    /// be a per-collector sub-score without losing its meaning — so it is a strategy-level channel with its
    /// own weight, computed over every signal surviving the strategy's <see cref="SignalTypeFilter"/> gate.
    /// It declares no collectors.
    /// </summary>
    Breadth = 1,
}

/// <summary>
/// ONE channel of a <c>radar-formula-v9</c> strategy (spec 146): a named, weighted slice of the strategy's
/// score with its own saturation constant.
/// <para>
/// The point of channels is that contributions become COMMENSURABLE. v8 computes every component over
/// whatever signals arrived, so a strategy cannot say "patents is half of my thesis" and a high-traffic
/// source silently dominates a high-value one. A channel array is a declared BUDGET: <c>Weight</c> is that
/// channel's share of the composite, and <c>Saturation</c> is how much of that channel's traffic counts as
/// a full share — mandatory per channel because RSS emits constantly and Form 4 rarely, and a shared
/// saturation would pin the chatty channel at 1.0, strand the rare one at the floor, and make the weights
/// decorative.
/// </para>
/// <para>
/// A channel that produces nothing scores 0 and the surviving weights are NOT renormalised — see
/// <see cref="ScoringChannelSet"/> for why that is the whole point.
/// </para>
/// </summary>
/// <param name="Name">
/// The channel's identity: unique within the strategy (compared case-insensitively), non-blank, and named in
/// the explanation, in each consumed signal's contribution reason, and in the per-channel provenance
/// breakdown.
/// </param>
/// <param name="Kind">Collector channel or the cross-source breadth channel.</param>
/// <param name="Collectors">
/// For a <see cref="ScoringChannelKind.Collector"/> channel, the <c>IEvidenceCollector.CollectorName</c>s
/// whose evidence this channel consumes — at least one, canonicalised to Ordinal-distinct + Ordinal-ordered
/// so the order they were listed in config is irrelevant. MUST be empty for a
/// <see cref="ScoringChannelKind.Breadth"/> channel. Matching is by EXACT (ordinal) collector name;
/// unknown names fail fast at startup rather than silently selecting nothing.
/// </param>
/// <param name="Weight">This channel's share of the composite, in <c>[0,1]</c>; the set's weights sum to 1.</param>
/// <param name="Saturation">
/// The channel's half-saturation constant (strictly positive), reusing the existing
/// <see cref="ScoringWeights.AttentionHalfSaturation"/> shape <c>x/(x+S)</c>: the raw magnitude at which the
/// channel reaches half its share.
/// </param>
public sealed record ScoringChannel(
    string Name,
    ScoringChannelKind Kind,
    IReadOnlyList<string> Collectors,
    double Weight,
    double Saturation)
{
    /// <summary>An empty, genuinely read-only collector list — the canonical value for a breadth channel.</summary>
    public static IReadOnlyList<string> NoCollectors { get; } =
        new ReadOnlyCollection<string>(Array.Empty<string>());

    /// <summary>
    /// Builds a <see cref="ScoringChannelKind.Collector"/> channel over <paramref name="collectors"/>,
    /// canonicalising the list (trimmed, blank-free, Ordinal-distinct, Ordinal-ordered) so two strategies
    /// that list the same collectors in different orders are the same strategy and hash identically.
    /// Validation of the values themselves is <see cref="ScoringChannelSet"/>'s job, so a misconfiguration is
    /// reported with the strategy name attached.
    /// <para>
    /// The de-dupe is ORDINAL, deliberately: collector names are matched exactly everywhere else (see
    /// <see cref="Consumes"/> and <c>ScoringStrategyFactory</c>'s registered-collector check), so collapsing
    /// <c>["patents", "Patents"]</c> case-insensitively would swallow the casing typo before it could reach
    /// that check — and which spelling survived would depend on config order. Keeping both means the invalid
    /// one fails fast at startup, which is the point of the exact match.
    /// </para>
    /// </summary>
    public static ScoringChannel Collector(
        string name, IEnumerable<string>? collectors, double weight, double saturation) =>
        new(name, ScoringChannelKind.Collector, Canonicalize(collectors), weight, saturation);

    /// <summary>Builds the cross-source <see cref="ScoringChannelKind.Breadth"/> channel (no collectors).</summary>
    public static ScoringChannel Breadth(string name, double weight, double saturation) =>
        new(name, ScoringChannelKind.Breadth, NoCollectors, weight, saturation);

    /// <summary>
    /// True when this channel consumes evidence retrieved by <paramref name="collectorName"/>. Always false
    /// for a null/blank name — legacy evidence with no recorded collector (see
    /// <c>CollectionProvenanceMetadata</c>) is consumed by no collector channel, and contributes 0.
    /// </summary>
    public bool Consumes(string? collectorName) =>
        !string.IsNullOrWhiteSpace(collectorName)
        && Collectors.Contains(collectorName, StringComparer.Ordinal);

    private static IReadOnlyList<string> Canonicalize(IEnumerable<string>? collectors)
    {
        if (collectors is null)
        {
            return NoCollectors;
        }

        var ordered = collectors
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        return ordered.Length == 0 ? NoCollectors : new ReadOnlyCollection<string>(ordered);
    }
}
