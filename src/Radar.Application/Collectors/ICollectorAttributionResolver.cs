using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

/// <summary>
/// The ONE seam through which scoring asks "which collector retrieved this evidence, and how do we know?"
/// (spec 151).
/// <para>
/// Before this interface existed the question was answered inline at its single consumption site
/// (<c>RadarScoreFormulaV9</c> called <see cref="CollectionProvenanceMetadata.Read(EvidenceItem?)"/>
/// directly), which made "recorded" the only expressible answer and left no place to add the legacy
/// inference without either forking the formula or making the inference unconditional. Routing the question
/// through an interface keeps the consumption site singular while letting the composition root decide the
/// POLICY — and, critically, keeps the default policy byte-identical to the pre-151 behaviour.
/// </para>
/// <para>
/// Implementations must be pure and deterministic (AD-3): no clock, no randomness, no I/O. They are resolved
/// once per process and called once per signal per scored company.
/// </para>
/// </summary>
public interface ICollectorAttributionResolver
{
    /// <summary>
    /// Resolves the collector behind <paramref name="evidence"/>. Never throws and never returns an
    /// attributed-but-nameless value; a null/unknown/ambiguous input resolves to
    /// <see cref="CollectorAttribution.Unattributed"/> rather than to a best guess.
    /// </summary>
    CollectorAttribution Resolve(EvidenceItem? evidence);
}
