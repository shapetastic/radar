using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Infrastructure.Tests.Persistence;

public class InMemorySignalReviewRepositoryTests
{
    private static SignalReview MakeReview(
        Guid id,
        Guid signalId,
        DateTimeOffset reviewedAtUtc,
        SignalReviewDecision decision = SignalReviewDecision.Approve)
        => new(
            Id: id,
            SignalId: signalId,
            ReviewerName: "DeterministicSignalReviewer",
            Decision: decision,
            Summary: "Reviewed.",
            IssuesJson: null,
            ReviewedAtUtc: reviewedAtUtc);

    [Fact]
    public async Task AddAsync_ThenGetByIdAsync_RoundTrips()
    {
        var repo = new InMemorySignalReviewRepository();
        var review = MakeReview(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));

        await repo.AddAsync(review, CancellationToken.None);

        var fetched = await repo.GetByIdAsync(review.Id, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(review, fetched);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var repo = new InMemorySignalReviewRepository();

        var fetched = await repo.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetBySignalAsync_ReturnsOnlyReviewsForRequestedSignal()
    {
        var repo = new InMemorySignalReviewRepository();
        var signalA = Guid.NewGuid();
        var signalB = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        var a1 = MakeReview(Guid.NewGuid(), signalA, ts);
        var a2 = MakeReview(Guid.NewGuid(), signalA, ts.AddMinutes(1));
        var b1 = MakeReview(Guid.NewGuid(), signalB, ts);

        await repo.AddAsync(a1, CancellationToken.None);
        await repo.AddAsync(a2, CancellationToken.None);
        await repo.AddAsync(b1, CancellationToken.None);

        var result = await repo.GetBySignalAsync(signalA, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(signalA, r.SignalId));
        Assert.Contains(result, r => r.Id == a1.Id);
        Assert.Contains(result, r => r.Id == a2.Id);
    }

    [Fact]
    public async Task GetBySignalAsync_UnknownSignal_ReturnsEmpty()
    {
        var repo = new InMemorySignalReviewRepository();
        await repo.AddAsync(
            MakeReview(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        var result = await repo.GetBySignalAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBySignalAsync_ReturnsOrderedByReviewedAtThenId()
    {
        var repo = new InMemorySignalReviewRepository();
        var signalId = Guid.NewGuid();

        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var first = MakeReview(Guid.NewGuid(), signalId, t1);
        var second = MakeReview(Guid.NewGuid(), signalId, t2);
        var third = MakeReview(Guid.NewGuid(), signalId, t3);

        // Insert out of order.
        await repo.AddAsync(third, CancellationToken.None);
        await repo.AddAsync(first, CancellationToken.None);
        await repo.AddAsync(second, CancellationToken.None);

        var result = await repo.GetBySignalAsync(signalId, CancellationToken.None);

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task GetBySignalAsync_EqualReviewedAt_BreaksTieById()
    {
        var repo = new InMemorySignalReviewRepository();
        var signalId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expected = new[] { idA, idB }.OrderBy(x => x).ToArray();

        await repo.AddAsync(MakeReview(idB, signalId, ts), CancellationToken.None);
        await repo.AddAsync(MakeReview(idA, signalId, ts), CancellationToken.None);

        var result = await repo.GetBySignalAsync(signalId, CancellationToken.None);

        Assert.Equal(expected, result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task AddAsync_SameId_UpsertsLastWriteWins()
    {
        var repo = new InMemorySignalReviewRepository();
        var id = Guid.NewGuid();
        var signalId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var original = MakeReview(id, signalId, ts, SignalReviewDecision.Approve);
        var updated = MakeReview(id, signalId, ts, SignalReviewDecision.EscalateToHuman);

        await repo.AddAsync(original, CancellationToken.None);
        await repo.AddAsync(updated, CancellationToken.None);

        var fetched = await repo.GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(SignalReviewDecision.EscalateToHuman, fetched!.Decision);

        // Upsert by Id: only one record remains for the signal.
        var bySignal = await repo.GetBySignalAsync(signalId, CancellationToken.None);
        Assert.Single(bySignal);
    }

    [Fact]
    public async Task AddAsync_DistinctIdsSameSignal_AllReturned()
    {
        var repo = new InMemorySignalReviewRepository();
        var signalId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var r1 = MakeReview(Guid.NewGuid(), signalId, ts);
        var r2 = MakeReview(Guid.NewGuid(), signalId, ts.AddMinutes(1));

        await repo.AddAsync(r1, CancellationToken.None);
        await repo.AddAsync(r2, CancellationToken.None);

        var result = await repo.GetBySignalAsync(signalId, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == r1.Id);
        Assert.Contains(result, r => r.Id == r2.Id);
    }
}
