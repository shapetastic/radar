using Radar.Domain.Evidence;

namespace Radar.Application.Abstractions.Persistence;

public interface IEvidenceRepository
{
    // Returns false if an item with the same ContentHash already exists (dedupe),
    // true if newly added. Preserves immutability: never overwrites existing evidence.
    Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct);
    Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct);
    Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct);
}
