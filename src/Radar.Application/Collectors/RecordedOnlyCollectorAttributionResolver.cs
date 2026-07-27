using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

/// <summary>
/// The DEFAULT <see cref="ICollectorAttributionResolver"/>: reads the collector name spec 146 recorded on the
/// evidence, and answers <see cref="CollectorAttribution.Unattributed"/> when there is none.
/// <para>
/// This is <b>behaviourally identical to the pre-spec-151 code</b> — the formula's inline
/// <see cref="CollectionProvenanceMetadata.Read(EvidenceItem?)"/> call, with the result wrapped so its
/// provenance is expressible. That equivalence is the point: it is what makes the spec-151 inference genuinely
/// opt-in, keeps every existing composition and test unaffected, and preserves spec 139's
/// <c>replay ⊆ forward</c> invariant by default (a replay and a forward run over the same evidence resolve the
/// same attribution unless an operator explicitly turns inference on).
/// </para>
/// </summary>
public sealed class RecordedOnlyCollectorAttributionResolver : ICollectorAttributionResolver
{
    /// <summary>
    /// The shared instance. The resolver is stateless and pure, so one instance serves the whole process; it
    /// is exposed so a type that takes an OPTIONAL resolver can default to it without a null check at every
    /// call site.
    /// </summary>
    public static RecordedOnlyCollectorAttributionResolver Instance { get; } = new();

    /// <inheritdoc />
    public CollectorAttribution Resolve(EvidenceItem? evidence)
    {
        // Read() already degrades a null item, a missing envelope, malformed JSON and a blank value to null
        // (skip-don't-throw), so "unrecorded" is the single failure mode and a score is never failed by an
        // unreadable metadata bag.
        var recorded = CollectionProvenanceMetadata.Read(evidence);

        return recorded is null
            ? CollectorAttribution.Unattributed
            : CollectorAttribution.Recorded(recorded);
    }
}
