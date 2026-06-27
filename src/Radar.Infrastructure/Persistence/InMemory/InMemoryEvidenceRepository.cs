using System.Collections.Concurrent;
using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Evidence;

namespace Radar.Infrastructure.Persistence.InMemory;

public sealed class InMemoryEvidenceRepository : IEvidenceRepository
{
    private readonly ConcurrentDictionary<Guid, EvidenceItem> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byContentHash = new();

    public Task<bool> AddIfNewAsync(EvidenceItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Atomic check-and-add on the content-hash index enforces the unique-hash
        // dedupe rule. If the hash already exists we reject without mutating the
        // existing (immutable) evidence.
        if (!_byContentHash.TryAdd(item.ContentHash, item.Id))
        {
            return Task.FromResult(false);
        }

        // Preserve immutability: never overwrite an existing record under the same
        // Id. If the Id is somehow already present, roll back the hash index entry
        // we just added so the two indexes stay consistent.
        if (!_byId.TryAdd(item.Id, item))
        {
            _byContentHash.TryRemove(item.ContentHash, out _);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task<EvidenceItem?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        _byId.TryGetValue(id, out var item);
        return Task.FromResult(item);
    }

    public Task<EvidenceItem?> GetByContentHashAsync(string contentHash, CancellationToken ct)
    {
        if (_byContentHash.TryGetValue(contentHash, out var id) && _byId.TryGetValue(id, out var item))
        {
            return Task.FromResult<EvidenceItem?>(item);
        }

        return Task.FromResult<EvidenceItem?>(null);
    }

    public Task<IReadOnlyList<EvidenceItem>> GetAllAsync(CancellationToken ct)
    {
        IReadOnlyList<EvidenceItem> result = _byId.Values.ToList();
        return Task.FromResult(result);
    }
}
