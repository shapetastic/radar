using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Abstractions.Persistence;
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
        UpdatedAtUtc: Now,
        Themes: []);

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

        return new CompanyResolver(repository, NullLogger<CompanyResolver>.Instance);
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
    public async Task Hint_MatchingTicker_ResolvesAtHintConfidence()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        // The mention alone would not match; the hint (ticker) drives resolution.
        var result = await resolver.ResolveAsync(
            "Some Vendor", new[] { "ACME" }, CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(0.95m, result.Confidence);
        Assert.Equal("Company hint match", result.Reason);
        Assert.Equal("ACME", result.MatchedAlias);
    }

    [Fact]
    public async Task Hint_TakesPrecedenceOverMentionMatch()
    {
        var resolver = await BuildResolverAsync(
            new[]
            {
                NewCompany(AcmeId, "Acme Corporation", "ACME"),
                NewCompany(GlobexId, "Globex Corporation", "GLBX"),
            },
            Array.Empty<CompanyAlias>());

        // Mention matches Globex by name, but a single unambiguous hint points to Acme: the hint wins.
        var result = await resolver.ResolveAsync(
            "Globex Corporation", new[] { "ACME" }, CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(0.95m, result.Confidence);
        Assert.Equal("Company hint match", result.Reason);
    }

    [Fact]
    public async Task AmbiguousHints_FallThroughToMentionLogic()
    {
        var resolver = await BuildResolverAsync(
            new[]
            {
                NewCompany(AcmeId, "Acme Corporation", "ACME"),
                NewCompany(GlobexId, "Globex Corporation", "GLBX"),
            },
            Array.Empty<CompanyAlias>());

        // Two hints matching two distinct companies are ambiguous: fall through to the mention,
        // which matches Globex by name.
        var result = await resolver.ResolveAsync(
            "Globex Corporation", new[] { "ACME", "GLBX" }, CancellationToken.None);

        Assert.Equal(GlobexId, result.CompanyId);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal("Exact name match", result.Reason);
    }

    [Fact]
    public async Task UnknownHint_FallsThrough_MentionStillResolves()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        var result = await resolver.ResolveAsync(
            "Acme Corporation", new[] { "ZZZZ" }, CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal("Exact name match", result.Reason);
    }

    [Fact]
    public async Task UnknownHint_AndUnmatchableMention_IsUnresolved()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        var result = await resolver.ResolveAsync(
            "Nonexistent Holdings", new[] { "ZZZZ" }, CancellationToken.None);

        Assert.Null(result.CompanyId);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal("No match", result.Reason);
    }

    [Fact]
    public async Task EmptyHints_MatchSingleArgBehaviour()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        var singleArg = await resolver.ResolveAsync("Acme Corporation", CancellationToken.None);
        var emptyHints = await resolver.ResolveAsync(
            "Acme Corporation", Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(singleArg.CompanyId, emptyHints.CompanyId);
        Assert.Equal(singleArg.Confidence, emptyHints.Confidence);
        Assert.Equal(singleArg.Reason, emptyHints.Reason);

        // Empty mention + empty hints still reports "Empty mention".
        var empty = await resolver.ResolveAsync(
            "   ", Array.Empty<string>(), CancellationToken.None);
        Assert.Null(empty.CompanyId);
        Assert.Equal("Empty mention", empty.Reason);
    }

    [Fact]
    public async Task Hint_MatchingNormalizedName_Resolves()
    {
        var resolver = await BuildResolverAsync(
            new[] { NewCompany(AcmeId, "Acme Corporation", "ACME") },
            Array.Empty<CompanyAlias>());

        // The hint matches by normalized name (not ticker); mention alone would not match.
        var result = await resolver.ResolveAsync(
            "Some Vendor", new[] { "Acme Corporation" }, CancellationToken.None);

        Assert.Equal(AcmeId, result.CompanyId);
        Assert.Equal(0.95m, result.Confidence);
        Assert.Equal("Company hint match", result.Reason);
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

    [Fact]
    public async Task BlankMentionAndBlankHints_DoesNotTouchRepository()
    {
        // The fast-path must short-circuit before any repository read when there is
        // nothing to resolve. The throwing repository fails the test if it is queried.
        var resolver = new CompanyResolver(new ThrowingCompanyRepository(), NullLogger<CompanyResolver>.Instance);

        var result = await resolver.ResolveAsync("   ", new[] { "", "  " }, CancellationToken.None);

        Assert.Null(result.CompanyId);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal("Empty mention", result.Reason);
    }

    // Repository whose read methods throw, so any access by the resolver fails the test.
    private sealed class ThrowingCompanyRepository : ICompanyRepository
    {
        public Task AddAsync(Company company, CancellationToken ct) => throw new InvalidOperationException();

        public Task<Company?> GetByIdAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException();

        public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken ct) => throw new InvalidOperationException();

        public Task AddAliasAsync(CompanyAlias alias, CancellationToken ct) => throw new InvalidOperationException();

        public Task<IReadOnlyList<CompanyAlias>> GetAliasesAsync(CancellationToken ct) => throw new InvalidOperationException();

        public Task AddSourceFeedAsync(CompanySourceFeed feed, CancellationToken ct) => throw new InvalidOperationException();

        public Task<IReadOnlyList<CompanySourceFeed>> GetSourceFeedsAsync(CancellationToken ct) => throw new InvalidOperationException();
    }
}
