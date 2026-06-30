namespace Radar.Application.Abstractions.Persistence;

public interface ISignalReviewRepository
{
    /// <remarks>
    /// Upsert by Id (last-write-wins), per AD-1. Review records carry a fresh Guid Id per review,
    /// so in practice this behaves as append-only — the relational implementation must preserve
    /// these semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddAsync(Radar.Domain.Signals.SignalReview review, CancellationToken ct);

    Task<Radar.Domain.Signals.SignalReview?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <remarks>Ordered by ReviewedAtUtc ascending, then Id (AD-3).</remarks>
    Task<IReadOnlyList<Radar.Domain.Signals.SignalReview>> GetBySignalAsync(Guid signalId, CancellationToken ct);
}
