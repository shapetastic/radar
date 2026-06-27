using Radar.Domain.Evidence;

namespace Radar.Application.Abstractions.Persistence;

public interface IEvidenceRepository
{
    /// <remarks>
    /// Insert-only: existing evidence is never overwritten (immutable); a duplicate
    /// ContentHash is rejected and returns false.
    /// </remarks>
    Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct);
    Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct);
    Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct);
}
