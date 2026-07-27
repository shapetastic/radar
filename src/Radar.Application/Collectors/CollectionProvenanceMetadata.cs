using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

/// <summary>
/// The ONE definition of "which collector retrieved this evidence" — the <c>collector</c> key inside the
/// evidence-metadata envelope, plus the reader that pulls it back out (spec 146).
/// <para>
/// WHY THIS EXISTS. Before this slice there was <b>no recorded collector on evidence</b>.
/// <see cref="EvidenceItem.SourceType"/> is an enum several collectors share (<c>sec-edgar</c>,
/// <c>sec-form4</c> and <c>sec-13dg</c> all emit <see cref="EvidenceSourceType.Filing"/>) and
/// <see cref="EvidenceItem.SourceName"/> is the FEED, not the collector; <c>CollectionResultMerger.Merge</c>
/// then concatenates every collector's results into one list and discards per-collector attribution
/// entirely. A <c>radar-formula-v9</c> collector channel selects on recorded provenance — so that provenance
/// had to be recorded first. It is stamped at ONE site (<c>RadarPipelineRunner</c>, in the loop that calls
/// each collector) rather than in twelve collectors, and read back through <see cref="Read(string?)"/> so
/// the key string is never hand-rolled at a second call site (CLAUDE.md reuse-over-copy).
/// </para>
/// <para>
/// SAFETY: the metadata bag is <b>not</b> an input to evidence identity and <b>not</b> an input to
/// <see cref="EvidenceItem.ContentHash"/>. Spec 145 derives the id from the normalized title+body hash
/// ALONE (collector, source name, URL, timestamps and the metadata bag are all explicitly excluded), so
/// stamping this key moves no evidence id, no content hash, no <c>AddIfNewAsync</c> dedupe decision and no
/// scoring fingerprint. It only widens the recorded provenance envelope.
/// </para>
/// <para>
/// TWO HONEST CAVEATS, both deliberate:
/// <list type="number">
/// <item><b>Accrued evidence has no <c>collector</c> key.</b> Everything collected before this slice was
/// persisted without it, and Radar's standing rule is that accrued history is never backfilled or rewritten
/// (specs 142/145). A collector channel therefore sees nothing for legacy evidence and contributes 0 for it —
/// which is exactly what a channel whose source is quiet contributes, and is why the v9 provenance records
/// whether each declared collector actually RAN.</item>
/// <item><b>Identical content from two collectors is ONE evidence record</b> (spec 145): identical
/// normalized content is one fact, and two collectors finding it is two retrieval paths. The recorded
/// collector is therefore the one whose item was mapped first in the run's stable collector order — every
/// contributing source still keeps its own raw file on disk, so no provenance is lost, but the single
/// identity index carries a single collector name.</item>
/// </list>
/// </para>
/// </summary>
public static class CollectionProvenanceMetadata
{
    /// <summary>
    /// The metadata key carrying <see cref="IEvidenceCollector.CollectorName"/>. Deliberately a plain,
    /// human-readable key inside the existing free-form bag rather than a new column on
    /// <see cref="EvidenceItem"/>: the bag already round-trips losslessly through
    /// <see cref="EvidenceMetadata"/> and through the durable raw-evidence store, so nothing else had to
    /// change to make it persist.
    /// </summary>
    public const string MetadataKey = "collector";

    /// <summary>
    /// Returns <paramref name="collected"/> with <see cref="MetadataKey"/> set to
    /// <paramref name="collectorName"/>. Pure: the input record is never mutated (its
    /// <see cref="CollectedEvidence.Metadata"/> may be any read-only dictionary, including a shared one), a
    /// fresh ordinal-keyed dictionary is built instead. An item already carrying the same value is returned
    /// unchanged; an item carrying a DIFFERENT value is overwritten, because the collector that actually
    /// produced this item is the authoritative answer.
    /// </summary>
    public static CollectedEvidence Stamp(CollectedEvidence collected, string collectorName)
    {
        ArgumentNullException.ThrowIfNull(collected);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectorName);

        if (collected.Metadata.TryGetValue(MetadataKey, out var existing)
            && string.Equals(existing, collectorName, StringComparison.Ordinal))
        {
            return collected;
        }

        var metadata = new Dictionary<string, string>(collected.Metadata, StringComparer.Ordinal)
        {
            [MetadataKey] = collectorName,
        };

        return collected with { Metadata = metadata };
    }

    /// <summary>
    /// Reads the recorded collector name off an <see cref="EvidenceItem"/>. Returns <c>null</c> when the
    /// evidence is null, carries no metadata envelope, or has no (or a blank) <see cref="MetadataKey"/> —
    /// i.e. for all legacy evidence. Never throws: <see cref="EvidenceMetadata.TryRead"/> degrades malformed
    /// JSON to "no usable metadata" (skip-don't-throw), so an unreadable envelope reads as "unrecorded"
    /// rather than failing a score.
    /// </summary>
    public static string? Read(EvidenceItem? evidence) => Read(evidence?.MetadataJson);

    /// <summary>
    /// Reads the recorded collector name out of a raw <see cref="EvidenceItem.MetadataJson"/> envelope.
    /// See <see cref="Read(EvidenceItem?)"/> for the null/legacy behaviour.
    /// </summary>
    public static string? Read(string? metadataJson)
    {
        if (!EvidenceMetadata.TryRead(metadataJson, out var metadata, out _))
        {
            return null;
        }

        return metadata.TryGetValue(MetadataKey, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
    }
}
