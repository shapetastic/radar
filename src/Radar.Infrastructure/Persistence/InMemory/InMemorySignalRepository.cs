using System.Collections.Concurrent;
using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Signals;

namespace Radar.Infrastructure.Persistence.InMemory;

public sealed class InMemorySignalRepository : ISignalRepository
{
    private readonly ConcurrentDictionary<Guid, Signal> _byId = new();

    public Task AddAsync(Signal signal, CancellationToken ct)
    {
        _byId[signal.Id] = signal;
        return Task.CompletedTask;
    }

    public Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        _byId.TryGetValue(id, out var signal);
        return Task.FromResult(signal);
    }

    public Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct)
    {
        IReadOnlyList<Signal> result = _byId.Values
            .Where(s => s.CompanyId == companyId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct)
    {
        // Window bounds are inclusive on ObservedAtUtc.
        IReadOnlyList<Signal> result = _byId.Values
            .Where(s => s.ObservedAtUtc >= startUtc && s.ObservedAtUtc <= endUtc)
            .ToList();
        return Task.FromResult(result);
    }
}
