using Microsoft.Extensions.Logging.Abstractions;
using Radar.Domain.Companies;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

public sealed class LocalFileCompanySeedSourceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _tempDir;

    public LocalFileCompanySeedSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore transient filesystem locks and permission errors.
        }
    }

    private string WriteSeedFile(string content)
    {
        var path = Path.Combine(_tempDir, "seed.json");
        File.WriteAllText(path, content);
        return path;
    }

    private LocalFileCompanySeedSource CreateSource(string filePath) =>
        new(
            new LocalFileCompanySeedOptions { FilePath = filePath },
            NullLogger<LocalFileCompanySeedSource>.Instance,
            new FixedTimeProvider(FixedNow));

    private const string TwoCompanyJson = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Acme Corp",
              "legalName": "Acme Corporation Inc.",
              "ticker": "ACME",
              "exchange": "NASDAQ",
              "countryCode": "US",
              "sector": "Technology",
              "industry": "Software",
              "aliases": [ "Acme", "Acme Inc" ]
            },
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "Globex",
              "ticker": "GLBX",
              "aliases": [ "Globex Industries" ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_ReadsCompaniesAndAliases()
    {
        var path = WriteSeedFile(TwoCompanyJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(2, seed.Companies.Count);

        var acme = seed.Companies[0];
        Assert.Equal(AcmeId, acme.Id);
        Assert.Equal("Acme Corp", acme.Name);
        Assert.Equal("ACME", acme.Ticker);
        Assert.Equal(CompanyStatus.Active, acme.Status);
        Assert.Equal(FixedNow, acme.CreatedAtUtc);
        Assert.Equal(FixedNow, acme.UpdatedAtUtc);

        var globex = seed.Companies[1];
        Assert.Equal(GlobexId, globex.Id);
        Assert.Equal("Globex", globex.Name);

        Assert.Equal(3, seed.Aliases.Count);
        Assert.All(seed.Aliases, alias =>
        {
            Assert.Equal("seed", alias.AliasType);
            Assert.Equal(FixedNow, alias.CreatedAtUtc);
        });

        Assert.Contains(seed.Aliases, a => a.CompanyId == AcmeId && a.Alias == "Acme");
        Assert.Contains(seed.Aliases, a => a.CompanyId == AcmeId && a.Alias == "Acme Inc");
        Assert.Contains(seed.Aliases, a => a.CompanyId == GlobexId && a.Alias == "Globex Industries");
    }

    [Fact]
    public async Task GetSeedAsync_DerivesDeterministicAliasIds()
    {
        var path = WriteSeedFile(TwoCompanyJson);

        var first = await CreateSource(path).GetSeedAsync(CancellationToken.None);
        var second = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(first.Aliases.Count, second.Aliases.Count);

        foreach (var alias in first.Aliases)
        {
            var match = second.Aliases.Single(
                a => a.CompanyId == alias.CompanyId && a.Alias == alias.Alias);
            Assert.Equal(alias.Id, match.Id);
        }

        // Distinct (companyId, aliasText) tuples yield distinct Ids.
        Assert.Equal(
            first.Aliases.Count,
            first.Aliases.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public async Task GetSeedAsync_MissingFile_ReturnsEmptyAndDoesNotThrow()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.json");

        var seed = await CreateSource(missing).GetSeedAsync(CancellationToken.None);

        Assert.Empty(seed.Companies);
        Assert.Empty(seed.Aliases);
    }

    [Fact]
    public async Task GetSeedAsync_MalformedJson_ReturnsEmptyAndDoesNotThrow()
    {
        var path = WriteSeedFile("{ this is not valid json ");

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Empty(seed.Companies);
        Assert.Empty(seed.Aliases);
    }

    [Fact]
    public async Task GetSeedAsync_SkipsEntriesMissingIdOrName_KeepsValidSiblings()
    {
        var path = WriteSeedFile("""
            {
              "companies": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Acme Corp",
                  "ticker": "ACME"
                },
                {
                  "id": "not-a-guid",
                  "name": "Bad Id Co"
                },
                {
                  "id": "33333333-3333-3333-3333-333333333333",
                  "name": "   "
                }
              ]
            }
            """);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        var company = Assert.Single(seed.Companies);
        Assert.Equal(AcmeId, company.Id);
        Assert.Equal("Acme Corp", company.Name);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
