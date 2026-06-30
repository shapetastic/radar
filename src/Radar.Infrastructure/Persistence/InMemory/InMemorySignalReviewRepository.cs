using System.Collections.Concurrent;
using Radar.Application.Abstractions.Persistence;
using Radar.Domain.Signals;

namespace Radar.Infrastructure.Persistence.InMemory;

public sealed class InMemorySignalReviewRepository : ISignalReviewRepository
{
    private readonly ConcurrentDictionary<Guid, SignalReview> _byId = new();

    public Task AddAsync(SignalReview review, CancellationToken ct)
    {
        _byId[review.Id] = review;
        return Task.CompletedTask;
    }

    public Task<SignalReview?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        _byId.TryGetValue(id, out var review);
        return Task.FromResult(review);
    }

    public Task<IReadOnlyList<SignalReview>> GetBySignalAsync(Guid signalId, CancellationToken ct)
    {
        IReadOnlyList<SignalReview> result = _byId.Values
            .Where(r => r.SignalId == signalId)
            .OrderBy(r => r.ReviewedAtUtc)
            .ThenBy(r => r.Id)
            .ToList();
        return Task.FromResult(result);
    }
}
