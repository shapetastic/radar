using Radar.Domain.Companies;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Infrastructure.Tests.Persistence;

public class InMemoryCompanyRepositoryTests
{
    private static Company MakeCompany(Guid id, DateTimeOffset createdAtUtc)
        => new(
            Id: id,
            Name: "Example Corp",
            LegalName: "Example Corporation Inc.",
            Ticker: "EXM",
            Exchange: "NASDAQ",
            CountryCode: "US",
            Sector: "Technology",
            Industry: "Software",
            Status: CompanyStatus.Active,
            CreatedAtUtc: createdAtUtc,
            UpdatedAtUtc: createdAtUtc,
            Themes: []);

    private static CompanyAlias MakeAlias(Guid id, Guid companyId, DateTimeOffset createdAtUtc)
        => new(
            Id: id,
            CompanyId: companyId,
            Alias: "Example",
            AliasType: "ShortName",
            CreatedAtUtc: createdAtUtc);

    private static CompanySourceFeed MakeFeed(Guid id, Guid companyId, DateTimeOffset createdAtUtc)
        => new(
            Id: id,
            CompanyId: companyId,
            FeedType: "rss",
            Name: "Example Investor News",
            Url: "https://example.com/rss",
            CreatedAtUtc: createdAtUtc);

    [Fact]
    public async Task GetAllAsync_ReturnsCompaniesOrderedByCreatedAtThenId()
    {
        var repo = new InMemoryCompanyRepository();

        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var first = MakeCompany(Guid.NewGuid(), t1);
        var second = MakeCompany(Guid.NewGuid(), t2);
        var third = MakeCompany(Guid.NewGuid(), t3);

        // Insert out of order to prove the repository sorts.
        await repo.AddAsync(third, CancellationToken.None);
        await repo.AddAsync(first, CancellationToken.None);
        await repo.AddAsync(second, CancellationToken.None);

        var result = await repo.GetAllAsync(CancellationToken.None);

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            result.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task GetAllAsync_EqualCreatedAt_BreaksTieById()
    {
        var repo = new InMemoryCompanyRepository();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expected = new[] { idA, idB }.OrderBy(x => x).ToArray();

        var a = MakeCompany(idA, ts);
        var b = MakeCompany(idB, ts);

        await repo.AddAsync(b, CancellationToken.None);
        await repo.AddAsync(a, CancellationToken.None);

        var result = await repo.GetAllAsync(CancellationToken.None);

        Assert.Equal(expected, result.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task GetAliasesAsync_ReturnsAliasesOrderedByCreatedAtThenId()
    {
        var repo = new InMemoryCompanyRepository();
        var companyId = Guid.NewGuid();

        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var first = MakeAlias(Guid.NewGuid(), companyId, t1);
        var second = MakeAlias(Guid.NewGuid(), companyId, t2);
        var third = MakeAlias(Guid.NewGuid(), companyId, t3);

        await repo.AddAliasAsync(second, CancellationToken.None);
        await repo.AddAliasAsync(third, CancellationToken.None);
        await repo.AddAliasAsync(first, CancellationToken.None);

        var result = await repo.GetAliasesAsync(CancellationToken.None);

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            result.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task GetAliasesAsync_EqualCreatedAt_BreaksTieById()
    {
        var repo = new InMemoryCompanyRepository();
        var companyId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expected = new[] { idA, idB }.OrderBy(x => x).ToArray();

        var a = MakeAlias(idA, companyId, ts);
        var b = MakeAlias(idB, companyId, ts);

        await repo.AddAliasAsync(b, CancellationToken.None);
        await repo.AddAliasAsync(a, CancellationToken.None);

        var result = await repo.GetAliasesAsync(CancellationToken.None);

        Assert.Equal(expected, result.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task AddSourceFeedAsync_ThenGet_ReturnsTheFeed()
    {
        var repo = new InMemoryCompanyRepository();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var feed = MakeFeed(Guid.NewGuid(), Guid.NewGuid(), ts);

        await repo.AddSourceFeedAsync(feed, CancellationToken.None);

        var result = await repo.GetSourceFeedsAsync(CancellationToken.None);

        var stored = Assert.Single(result);
        Assert.Equal(feed, stored);
    }

    [Fact]
    public async Task AddSourceFeedAsync_SameId_Upserts_LastWriteWins()
    {
        var repo = new InMemoryCompanyRepository();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        var original = MakeFeed(id, companyId, ts);
        var updated = original with { Name = "Renamed Feed", Url = "https://example.com/new-rss" };

        await repo.AddSourceFeedAsync(original, CancellationToken.None);
        await repo.AddSourceFeedAsync(updated, CancellationToken.None);

        var result = await repo.GetSourceFeedsAsync(CancellationToken.None);

        var stored = Assert.Single(result);
        Assert.Equal("Renamed Feed", stored.Name);
        Assert.Equal("https://example.com/new-rss", stored.Url);
    }

    [Fact]
    public async Task GetSourceFeedsAsync_ReturnsFeedsOrderedByCreatedAtThenId()
    {
        var repo = new InMemoryCompanyRepository();
        var companyId = Guid.NewGuid();

        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        var first = MakeFeed(Guid.NewGuid(), companyId, t1);
        var second = MakeFeed(Guid.NewGuid(), companyId, t2);
        var third = MakeFeed(Guid.NewGuid(), companyId, t3);

        await repo.AddSourceFeedAsync(second, CancellationToken.None);
        await repo.AddSourceFeedAsync(third, CancellationToken.None);
        await repo.AddSourceFeedAsync(first, CancellationToken.None);

        var result = await repo.GetSourceFeedsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            result.Select(f => f.Id).ToArray());
    }

    [Fact]
    public async Task GetSourceFeedsAsync_EqualCreatedAt_BreaksTieById()
    {
        var repo = new InMemoryCompanyRepository();
        var companyId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var expected = new[] { idA, idB }.OrderBy(x => x).ToArray();

        var a = MakeFeed(idA, companyId, ts);
        var b = MakeFeed(idB, companyId, ts);

        await repo.AddSourceFeedAsync(b, CancellationToken.None);
        await repo.AddSourceFeedAsync(a, CancellationToken.None);

        var result = await repo.GetSourceFeedsAsync(CancellationToken.None);

        Assert.Equal(expected, result.Select(f => f.Id).ToArray());
    }
}
