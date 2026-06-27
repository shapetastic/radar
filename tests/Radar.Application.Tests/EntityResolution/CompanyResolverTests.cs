using Radar.Application.EntityResolution;
using Radar.Domain.Companies;
using Radar.Infrastructure.Persistence.InMemory;

namespace Radar.Application.Tests.EntityResolution;

public class CompanyResolverTests
{
    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Company NewCompany(Guid id, string name, string? ticker) => new(
        id,
        name,
        LegalName: null,
        Ticker: ticker,
        Exchange: null,
        CountryCode: null,
        Sector: null,
        Industry: null,
        Status: CompanyStatus.Active,
        CreatedAtUtc: Now,
        UpdatedAtUtc: Now);

    private static CompanyAlias NewAlias(Guid companyId, string alias) => new(
        Guid.NewGuid(),
        companyId,
        alias,
        AliasType: "brand",
        CreatedAtUtc: Now);

    private static async Task<CompanyResolver> BuildResolverAsync(
        IEnumerable<Company> companies,
        IEnumerable<CompanyAlias> aliases)
    {
        var repository = new InMemoryCompanyRepository();
        foreach (var company in companies)
        {
            await repository.AddAsync(company, CancellationToken.None);
        }

        foreach (var alias in aliases)
        {
            await repository.AddAliasAsync(alias, CancellationToken.None);
        }

        return new CompanyResolver(repository);
    }

    [Fact]
    public async Task ExactNameMatch_ResolvesAtFullConfidence()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        var result = await resolver.ResolveAsync("Acme Corporation", CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal("Exact name match", result.Reason);
    }

    [Fact]
    public async Task ExactAliasMatch_ResolvesAndReportsMatchedAlias()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            new[] { NewAlias(AcmeId, "Acme Widgets") });

        var result = await resolver.ResolveAsync("Acme Widgets", CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal("Exact alias match", result.Reason);
        Assert.Equal("acme widgets", result.MatchedAlias);
    }

    [Fact]
    public async Task SuffixCaseAndWhitespaceVariations_StillResolve()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            new[] { NewAlias(AcmeId, "acme") });

        // "Acme, Inc." normalizes to "acme" (punctuation + trailing suffix stripped),
        // matching the alias "acme"; extra/mixed-case whitespace is also collapsed.
        var result = await resolver.ResolveAsync("  Acme,   Inc.  ", CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal("acme", result.MatchedAlias);
    }

    [Fact]
    public async Task ExactTickerMatch_ResolvesAtNinetyPercentConfidence()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACM") },
            Array.Empty<CompanyAlias>());

        // "acm" is the ticker; it is not a normalized name/alias, so it resolves via the
        // case-insensitive raw-ticker path. Mixed case confirms case-insensitivity.
        var result = await resolver.ResolveAsync("Acm", CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(0.9m, result.Confidence);
        Assert.Equal("Exact ticker match", result.Reason);
        Assert.Null(result.MatchedAlias);
    }

    [Fact]
    public async Task UnknownMention_IsUnresolved()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        var result = await resolver.ResolveAsync("Nonexistent Holdings", CancellationToken.None);

        Assert.Null(result.CompanyId);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal("No match", result.Reason);
        Assert.Null(result.MatchedAlias);
    }

    [Fact]
    public async Task AliasSharedByTwoCompanies_IsAmbiguousAndUnresolved()
    {
        var resolver = await BuildResolverAsync(
            new[]
            {
                NewCompany(AcmeId, "Acme Corporation", "ACME"),
                NewCompany(GlobexId, "Globex Corporation", "GLBX"),
            },
            new[]
            {
                NewAlias(AcmeId, "Apex"),
                NewAlias(GlobexId, "Apex"),
            });

        var result = await resolver.ResolveAsync("Apex", CancellationToken.None);

        Assert.Null(result.CompanyId);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal("Ambiguous mention", result.Reason);
    }

    [Fact]
    public async Task EmptyOrWhitespaceMention_IsUnresolvedWithEmptyMentionReason()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        var result = await resolver.ResolveAsync("   ", CancellationToken.None);

        Assert.Null(result.CompanyId);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal("Empty mention", result.Reason);
    }

    [Fact]
    public async Task NullMention_Throws()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => resolver.ResolveAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task SubstringMention_DoesNotResolve()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        // A mention that merely contains the company name as a substring must not match.
        var result = await resolver.ResolveAsync(
            "We spoke to Acme Corporation executives", CancellationToken.None);

        Assert.Null(result.CompanyId);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal("No match", result.Reason);
    }

    [Fact]
    public async Task SuffixInsideWord_IsNotStripped()
    {
        // "Incoco" ends with the letters of "inc"/"co" but is a single whole token,
        // so normalization must leave it intact and resolve cleanly.
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Incoco", "INCO") },
            Array.Empty<CompanyAlias>());

        var result = await resolver.ResolveAsync("Incoco", CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal("Exact name match", result.Reason);
    }
}
