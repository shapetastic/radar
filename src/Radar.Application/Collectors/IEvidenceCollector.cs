namespace Radar.Application.Collectors;

using Radar.Domain.Evidence;

public interface IEvidenceCollector
{
    Task<IReadOnlyList<EvidenceItem>> CollectAsync(CancellationToken ct);
}
