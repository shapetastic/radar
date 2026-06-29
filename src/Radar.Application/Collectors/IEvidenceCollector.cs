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

    /// <summary>
    /// Collects raw evidence over the watch universe and returns it alongside an observational
    /// <see cref="CollectionSummary"/> describing this run's collection health.
    /// </summary>
    Task<CollectionResult> CollectAsync(
        CollectionContext context, CancellationToken ct);
}
