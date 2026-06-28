using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Domain.Companies;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Persistence.InMemory;
using Radar.TestSupport;

namespace Radar.Application.Tests.EntityResolution;

public class CompanyUniverseSeederTests
{
    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AcmeAliasId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GlobexAliasId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AcmeFeedId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid GlobexFeedId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CompanySeedData BuildSeed()
    {
        var acme = new CompanyBuilder()
            .WithId(AcmeId)
            .WithName("Acme Corp")
            .WithTicker("ACME")
            .Build();
        var globex = new CompanyBuilder()
            .WithId(GlobexId)
            .WithName("Globex")
            .WithTicker("GLBX")
            .Build();

        var aliases = new[]
        {
            new CompanyAlias(AcmeAliasId, AcmeId, "Acme Incorporated", "seed", Now),
            new CompanyAlias(GlobexAliasId, GlobexId, "Globex Industries", "seed", Now),
        };

        var feeds = new[]
        {
            new CompanySourceFeed(AcmeFeedId, AcmeId, "rss", "Acme Investor News", "https://example.com/acme.rss", Now),
            new CompanySourceFeed(GlobexFeedId, GlobexId, "rss", "Globex Investor News", "https://example.com/globex.rss", Now),
        };

        return new CompanySeedData(new[] { acme, globex }, aliases, feeds);
    }

    private static CompanyUniverseSeeder CreateSeeder(
        ICompanyRepository repository, CompanySeedData seed) =>
        new(
            new StubSeedSource(seed),
            repository,
            NullLogger<CompanyUniverseSeeder>.Instance);

    [Fact]
    public async Task SeedAsync_SeedsCompaniesAndAliases_AndReturnsCompanyCount()
    {
        var repository = new InMemoryCompanyRepository();
        var seed = BuildSeed();

        var count = await CreateSeeder(repository, seed).SeedAsync(CancellationToken.None);

        Assert.Equal(2, count);

        var companies = await repository.GetAllAsync(CancellationToken.None);
        Assert.Equal(2, companies.Count);
        Assert.Contains(companies, c => c.Id == AcmeId && c.Name == "Acme Corp");
        Assert.Contains(companies, c => c.Id == GlobexId && c.Name == "Globex");

        var aliases = await repository.GetAliasesAsync(CancellationToken.None);
        Assert.Equal(2, aliases.Count);
        Assert.Contains(aliases, a => a.Id == AcmeAliasId && a.CompanyId == AcmeId);
        Assert.Contains(aliases, a => a.Id == GlobexAliasId && a.CompanyId == GlobexId);

        var feeds = await repository.GetSourceFeedsAsync(CancellationToken.None);
        Assert.Equal(2, feeds.Count);
        Assert.Contains(feeds, f => f.Id == AcmeFeedId && f.CompanyId == AcmeId);
        Assert.Contains(feeds, f => f.Id == GlobexFeedId && f.CompanyId == GlobexId);
    }

    [Fact]
    public async Task SeedAsync_RunTwice_IsIdempotent()
    {
        var repository = new InMemoryCompanyRepository();
        var seed = BuildSeed();
        var seeder = CreateSeeder(repository, seed);

        await seeder.SeedAsync(CancellationToken.None);
        await seeder.SeedAsync(CancellationToken.None);

        var companies = await repository.GetAllAsync(CancellationToken.None);
        var aliases = await repository.GetAliasesAsync(CancellationToken.None);
        var feeds = await repository.GetSourceFeedsAsync(CancellationToken.None);

        Assert.Equal(2, companies.Count);
        Assert.Equal(2, aliases.Count);
        Assert.Equal(2, feeds.Count);
    }

    [Fact]
    public async Task SeedAsync_EnablesResolution_OfSeededNameAndAlias()
    {
        var repository = new InMemoryCompanyRepository();
        var seed = BuildSeed();

        await CreateSeeder(repository, seed).SeedAsync(CancellationToken.None);

        var resolver = new CompanyResolver(repository, NullLogger<CompanyResolver>.Instance);

        var byName = await resolver.ResolveAsync("Acme Corp", CancellationToken.None);
        Assert.Equal(AcmeId, byName.CompanyId);

        var byAlias = await resolver.ResolveAsync("Globex Industries", CancellationToken.None);
        Assert.Equal(GlobexId, byAlias.CompanyId);
    }

    [Fact]
    public async Task AddLocalFileCompanySeed_WiresSeeder_AndPopulatesRepository()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        await File.WriteAllTextAsync(tempFile, """
            {
              "companies": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Acme Corp",
                  "ticker": "ACME",
                  "aliases": [ "Acme" ]
                }
              ]
            }
            """);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services
                .AddInMemoryRadarPersistence()
                .AddLocalFileCompanySeed(tempFile);
            using var provider = services.BuildServiceProvider();

            var seeder = provider.GetRequiredService<ICompanyUniverseSeeder>();
            var count = await seeder.SeedAsync(CancellationToken.None);

            Assert.Equal(1, count);

            var repository = provider.GetRequiredService<ICompanyRepository>();
            var companies = await repository.GetAllAsync(CancellationToken.None);
            Assert.Single(companies);
            Assert.Equal(AcmeId, companies[0].Id);

            var aliases = await repository.GetAliasesAsync(CancellationToken.None);
            Assert.Single(aliases);
            Assert.Equal(AcmeId, aliases[0].CompanyId);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private sealed class StubSeedSource(CompanySeedData seed) : ICompanySeedSource
    {
        public Task<CompanySeedData> GetSeedAsync(CancellationToken ct) => Task.FromResult(seed);
    }
}
