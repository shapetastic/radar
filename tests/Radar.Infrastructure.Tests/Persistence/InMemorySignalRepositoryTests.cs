using Radar.Domain.Signals;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Infrastructure.Tests.Persistence;

public class InMemorySignalRepositoryTests
{
    private static Signal MakeSignal(
        Guid id,
        Guid? companyId,
        DateTimeOffset observedAtUtc)
        => new(
            Id: id,
            EvidenceId: Guid.NewGuid(),
            CompanyId: companyId,
            CompanyMention: "Example Corp",
            Type: SignalType.CustomerWin,
            Direction: SignalDirection.Positive,
            Strength: 3,
            Novelty: 2,
            Confidence: 0.8m,
            SupportingExcerpt: "won a major customer",
            Reason: "named customer in press release",
            ReviewStatus: SignalReviewStatus.Approved,
            ObservedAtUtc: observedAtUtc,
            CreatedAtUtc: new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task GetByCompanyAsync_ReturnsOnlySignalsForRequestedCompany()
    {
        var repo = new InMemorySignalRepository();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        var a1 = MakeSignal(Guid.NewGuid(), companyA, ts);
        var a2 = MakeSignal(Guid.NewGuid(), companyA, ts);
        var b1 = MakeSignal(Guid.NewGuid(), companyB, ts);
        var unattributed = MakeSignal(Guid.NewGuid(), null, ts);

        await repo.AddAsync(a1, CancellationToken.None);
        await repo.AddAsync(a2, CancellationToken.None);
        await repo.AddAsync(b1, CancellationToken.None);
        await repo.AddAsync(unattributed, CancellationToken.None);

        var result = await repo.GetByCompanyAsync(companyA, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, s => Assert.Equal(companyA, s.CompanyId));
        Assert.Contains(result, s => s.Id == a1.Id);
        Assert.Contains(result, s => s.Id == a2.Id);
    }

    [Fact]
    public async Task GetObservedBetweenAsync_ReturnsOnlySignalsInInclusiveWindow()
    {
        var repo = new InMemorySignalRepository();
        var company = Guid.NewGuid();

        var start = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);

        var before = MakeSignal(Guid.NewGuid(), company, start.AddTicks(-1));
        var onStart = MakeSignal(Guid.NewGuid(), company, start);
        var inside = MakeSignal(Guid.NewGuid(), company, new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        var onEnd = MakeSignal(Guid.NewGuid(), company, end);
        var after = MakeSignal(Guid.NewGuid(), company, end.AddTicks(1));

        await repo.AddAsync(before, CancellationToken.None);
        await repo.AddAsync(onStart, CancellationToken.None);
        await repo.AddAsync(inside, CancellationToken.None);
        await repo.AddAsync(onEnd, CancellationToken.None);
        await repo.AddAsync(after, CancellationToken.None);

        var result = await repo.GetObservedBetweenAsync(start, end, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, s => s.Id == onStart.Id);
        Assert.Contains(result, s => s.Id == inside.Id);
        Assert.Contains(result, s => s.Id == onEnd.Id);
        Assert.DoesNotContain(result, s => s.Id == before.Id);
        Assert.DoesNotContain(result, s => s.Id == after.Id);
    }

    [Fact]
    public async Task GetByCompanyAsync_ReturnsOrderedByObservedAtThenId()
    {
        var repo = new InMemorySignalRepository();
        var company = Guid.NewGuid();

        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var first = MakeSignal(Guid.NewGuid(), company, t1);
        var second = MakeSignal(Guid.NewGuid(), company, t2);
        var third = MakeSignal(Guid.NewGuid(), company, t3);

        await repo.AddAsync(third, CancellationToken.None);
        await repo.AddAsync(first, CancellationToken.None);
        await repo.AddAsync(second, CancellationToken.None);

        var result = await repo.GetByCompanyAsync(company, CancellationToken.None);

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task GetByCompanyAsync_EqualObservedAt_BreaksTieById()
    {
        var repo = new InMemorySignalRepository();
        var company = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expected = new[] { idA, idB }.OrderBy(x => x).ToArray();

        await repo.AddAsync(MakeSignal(idB, company, ts), CancellationToken.None);
        await repo.AddAsync(MakeSignal(idA, company, ts), CancellationToken.None);

        var result = await repo.GetByCompanyAsync(company, CancellationToken.None);

        Assert.Equal(expected, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task GetObservedBetweenAsync_ReturnsOrderedByObservedAtThenId()
    {
        var repo = new InMemorySignalRepository();
        var company = Guid.NewGuid();

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

        var t1 = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

        var first = MakeSignal(Guid.NewGuid(), company, t1);
        var second = MakeSignal(Guid.NewGuid(), company, t2);
        var third = MakeSignal(Guid.NewGuid(), company, t3);

        await repo.AddAsync(third, CancellationToken.None);
        await repo.AddAsync(first, CancellationToken.None);
        await repo.AddAsync(second, CancellationToken.None);

        var result = await repo.GetObservedBetweenAsync(start, end, CancellationToken.None);

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task GetObservedBetweenAsync_EqualObservedAt_BreaksTieById()
    {
        var repo = new InMemorySignalRepository();
        var company = Guid.NewGuid();

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var ts = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expected = new[] { idA, idB }.OrderBy(x => x).ToArray();

        await repo.AddAsync(MakeSignal(idB, company, ts), CancellationToken.None);
        await repo.AddAsync(MakeSignal(idA, company, ts), CancellationToken.None);

        var result = await repo.GetObservedBetweenAsync(start, end, CancellationToken.None);

        Assert.Equal(expected, result.Select(s => s.Id).ToArray());
    }
}
