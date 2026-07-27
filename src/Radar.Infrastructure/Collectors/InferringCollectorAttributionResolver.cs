using Radar.Application.Collectors;
using Radar.Domain.Evidence;

namespace Radar.Infrastructure.Collectors;

/// <summary>
/// The opt-in <see cref="ICollectorAttributionResolver"/> (spec 151): recorded attribution first, the
/// <see cref="LegacyCollectorAttributionInference"/> table only when nothing was recorded.
/// <para>
/// <b>RECORDED ALWAYS WINS, unconditionally.</b> Not "when they agree", not "when the inference is
/// confident" — always. The producing collector's own stamp is the authoritative answer; the table is a
/// reconstruction of the answer that stamp would have given had it existed. Consulting the inference only in
/// the absence of a stamp is also what makes this resolver strictly ADDITIVE over already-attributed
/// evidence: for every record spec 146 stamped, this resolver and
/// <see cref="RecordedOnlyCollectorAttributionResolver"/> return the identical value, so no already-produced
/// score can move and <c>replay ⊆ forward</c> is unaffected for the attributed cohort.
/// </para>
/// <para>
/// Registered only when <see cref="CollectorAttributionOptions.InferLegacyAttribution"/> is set. Pure,
/// deterministic, stateless (AD-3).
/// </para>
/// </summary>
internal sealed class InferringCollectorAttributionResolver : ICollectorAttributionResolver
{
    /// <inheritdoc />
    public CollectorAttribution Resolve(EvidenceItem? evidence)
    {
        var recorded = CollectionProvenanceMetadata.Read(evidence);
        if (recorded is not null)
        {
            return CollectorAttribution.Recorded(recorded);
        }

        var inferred = LegacyCollectorAttributionInference.Infer(evidence);

        return inferred is null
            ? CollectorAttribution.Unattributed
            : CollectorAttribution.Inferred(inferred);
    }
}
