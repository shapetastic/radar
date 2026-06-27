using Radar.Domain.Signals;

namespace Radar.Application.Abstractions.Persistence;

public interface ISignalRepository
{
    Task AddAsync(Signal signal, CancellationToken ct);
    Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct);
}
