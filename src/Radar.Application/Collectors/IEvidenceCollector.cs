using Radar.Domain.Evidence;

namespace Radar.Application.Collectors;

public interface IEvidenceCollector
{
    Task<IReadOnlyList<EvidenceItem>> CollectAsync(CancellationToken ct);
}
