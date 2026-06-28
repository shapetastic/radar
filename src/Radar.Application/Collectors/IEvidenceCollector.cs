namespace Radar.Application.Collectors;

public interface IEvidenceCollector
{
    /// <summary>Stable identifier of the concrete collector (provenance / logging).</summary>
    string CollectorName { get; }

    /// <summary>Canonical source-type token this collector emits (e.g. "local_file").</summary>
    string SourceType { get; }

    Task<IReadOnlyCollection<CollectedEvidence>> CollectAsync(
        CollectionContext context, CancellationToken cancellationToken);
}
