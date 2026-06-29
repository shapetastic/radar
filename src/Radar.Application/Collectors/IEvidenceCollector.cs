using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

public interface IEvidenceCollector
{
    /// <summary>Stable identifier of the concrete collector (provenance / logging).</summary>
    string CollectorName { get; }

    /// <summary>
    /// Canonical <see cref="EvidenceSourceType"/> this collector attributes its emitted evidence to.
    /// Strongly typed so a collector cannot silently mis-declare its provenance.
    /// </summary>
    EvidenceSourceType SourceType { get; }

    Task<IReadOnlyCollection<CollectedEvidence>> CollectAsync(
        CollectionContext context, CancellationToken ct);
}
