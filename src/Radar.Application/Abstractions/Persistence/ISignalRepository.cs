using Radar.Domain.Signals;

namespace Radar.Application.Abstractions.Persistence;

public interface ISignalRepository
{
    /// <remarks>
    /// Upsert by Id (last-write-wins). The relational implementation must preserve these
    /// semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddAsync(Signal signal, CancellationToken ct);
    Task<Signal?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Signal>> GetByCompanyAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<Signal>> GetObservedBetweenAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct);
}
