using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Collectors;
using Radar.Application.EntityResolution;
using Radar.Application.Pipeline;
using Radar.Domain.Companies;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// Spec 161 — the <c>Radar:Companies</c> seed-source decorator. The filter is applied at this ONE choke
/// point, so these tests are what guarantee that a filtered pass collects for exactly the named companies and
/// that no excluded company's feed survives to be collected.
/// </summary>
public sealed class FilteredCompanySeedSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid CassId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdtId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CatId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>A three-company seed, each with one alias and two feeds, in a fixed order.</summary>
    private static CompanySeedData BuildSeed() => new(
        [
            NewCompany(CassId, "Cass Information Systems", "CASS"),
            NewCompany(IdtId, "IDT Corporation", "IDT"),
            NewCompany(CatId, "Caterpillar Inc.", "CAT"),
        ],
        [
            NewAlias(CassId, "Cass"),
            NewAlias(IdtId, "IDT"),
            NewAlias(CatId, "Caterpillar"),
        ],
        [
            NewFeed(CassId, "rss", "https://example.com/cass.xml"),
            NewFeed(CassId, "sec", "https://example.com/cass.json"),
            NewFeed(IdtId, "rss", "https://example.com/idt.xml"),
            NewFeed(CatId, "rss", "https://example.com/cat.xml"),
            NewFeed(CatId, "sec", "https://example.com/cat.json"),
        ]);

    private static Company NewCompany(Guid id, string name, string? ticker) =>
        new(
            Id: id,
            Name: name,
            LegalName: name,
            Ticker: ticker,
            Exchange: "NASDAQ",
            CountryCode: "US",
            Sector: null,
            Industry: null,
            Status: CompanyStatus.Active,
            CreatedAtUtc: Now,
            UpdatedAtUtc: Now,
            Themes: []);

    private static CompanyAlias NewAlias(Guid companyId, string alias) =>
        new(Guid.NewGuid(), companyId, alias, "seed", Now);

    private static CompanySourceFeed NewFeed(Guid companyId, string type, string url) =>
        new(Guid.NewGuid(), companyId, type, $"{type} feed", url, Now);

    private static FilteredCompanySeedSource Filtered(
        CompanySeedData seed, params string[] tickers) =>
        new(
            new StubSeedSource(seed),
            CompanyFilter.FromTickers(tickers),
            NullLogger<FilteredCompanySeedSource>.Instance);

    // ---- retention -----------------------------------------------------------------------------------

    [Fact]
    public async Task Retains_ExactlyTheNamedCompanies_WithTheirAliasesAndFeeds()
    {
        var seed = BuildSeed();

        var filtered = await Filtered(seed, "CASS", "IDT").GetSeedAsync(default);

        Assert.Equal([CassId, IdtId], filtered.Companies.Select(c => c.Id));
        Assert.Equal([CassId, IdtId], filtered.Aliases.Select(a => a.CompanyId));
        Assert.Equal(
            [CassId, CassId, IdtId], filtered.SourceFeeds.Select(f => f.CompanyId));
    }

    [Fact]
    public async Task NoExcludedCompanyFeedSurvives()
    {
        // The load-bearing consistency rule: a feed surviving its excluded company would collect evidence
        // that resolves to a company the repository does not hold.
        var seed = BuildSeed();

        var filtered = await Filtered(seed, "CASS").GetSeedAsync(default);

        Assert.DoesNotContain(filtered.SourceFeeds, f => f.CompanyId == IdtId || f.CompanyId == CatId);
        Assert.DoesNotContain(filtered.Aliases, a => a.CompanyId == IdtId || a.CompanyId == CatId);
        Assert.All(
            filtered.SourceFeeds,
            f => Assert.Contains(filtered.Companies, c => c.Id == f.CompanyId));
    }

    [Theory]
    [InlineData("cass")]
    [InlineData("CaSs")]
    [InlineData("  cass  ")]
    public async Task TokenMatching_IsCaseInsensitiveAndTrimmed(string token)
    {
        var filtered = await Filtered(BuildSeed(), token).GetSeedAsync(default);

        Assert.Equal([CassId], filtered.Companies.Select(c => c.Id));
    }

    [Fact]
    public async Task DuplicateTokens_CollapseToOneCompany()
    {
        var filtered = await Filtered(BuildSeed(), "CASS", "cass", " Cass ").GetSeedAsync(default);

        Assert.Single(filtered.Companies);
    }

    [Fact]
    public async Task PreservesInnerOrder_AndNeverMutatesTheInnerSeed()
    {
        var seed = BuildSeed();
        var innerCompanyOrder = seed.Companies.Select(c => c.Id).ToList();
        var innerFeedCount = seed.SourceFeeds.Count;

        // Tickers deliberately supplied in the REVERSE of seed order: retention follows the SEED, not the
        // configured order, so the pass is deterministic whatever the operator typed (AD-3).
        var filtered = await Filtered(seed, "CAT", "CASS").GetSeedAsync(default);

        Assert.Equal([CassId, CatId], filtered.Companies.Select(c => c.Id));
        Assert.Equal(innerCompanyOrder, seed.Companies.Select(c => c.Id));
        Assert.Equal(innerFeedCount, seed.SourceFeeds.Count);
    }

    [Fact]
    public async Task WholeUniverseFilter_IsANoOpOverTheSeedContent()
    {
        var seed = BuildSeed();

        var filtered = await Filtered(seed, "CASS", "IDT", "CAT").GetSeedAsync(default);

        Assert.Equal(seed.Companies, filtered.Companies);
        Assert.Equal(seed.Aliases, filtered.Aliases);
        Assert.Equal(seed.SourceFeeds, filtered.SourceFeeds);
    }

    // ---- fail fast, never fail open ------------------------------------------------------------------

    [Fact]
    public async Task UnknownTicker_FailsFast_NamingTheTokenAndTheSeedTickerCount()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Filtered(BuildSeed(), "CASS", "NOPE").GetSeedAsync(default));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'NOPE'", ex.Message, StringComparison.Ordinal);
        // The COUNT, not all 74 tickers.
        Assert.Contains("holds 3 ticker(s)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTicker_NamesCheapNearMisses_WhenThereAreAny()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Filtered(BuildSeed(), "CAS").GetSeedAsync(default));

        Assert.Contains("'CAS'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean: CASS?", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryUnknownTicker_IsNamed_NotJustTheFirst()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Filtered(BuildSeed(), "NOPE", "ALSONOPE").GetSeedAsync(default));

        Assert.Contains("'NOPE'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'ALSONOPE'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySeed_FailsFast_RatherThanSilentlyCollectingNothing()
    {
        // The local-file seed source degrades to an empty seed when the file is missing/unreadable. Under a
        // filter that must be a failure, not a run that "worked".
        var empty = new CompanySeedData([], [], []);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Filtered(empty, "CASS").GetSeedAsync(default));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'CASS'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompanyWithNoTicker_IsNeverMatched()
    {
        var seed = new CompanySeedData([NewCompany(CassId, "Tickerless Holdings", null)], [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Filtered(seed, "CASS").GetSeedAsync(default));
    }

    // ---- downstream: collection health reconciles against the FILTERED inventory ---------------------

    /// <summary>
    /// Spec 161 implementation checkpoint, verified rather than argued. <see cref="SeedFeedInventoryValidator"/>
    /// is the only <c>ICollectionHealthValidator</c>, and it re-reads <see cref="ICompanySeedSource"/> — which
    /// under a filter IS this decorator. Two facts at once:
    /// <list type="number">
    /// <item>its rules are purely per-feed-type RELATIVE shrinkage with NO absolute minimum, so a ~2-source
    /// filtered run reconciles exactly as a ~300-source one does; and</item>
    /// <item>because it reads the decorated source, it compares the FILTERED declared inventory against the
    /// FILTERED collection context, so a partial pass emits no spurious feeds-lost warnings.</item>
    /// </list>
    /// The negative control pins that the decoration is what makes it hold: the UNFILTERED seed over the same
    /// filtered context warns. It lives here, beside the decorator, rather than in the validator's own test
    /// class — the validator's own rules are covered there; this is the decorator's downstream effect.
    /// </summary>
    [Fact]
    public async Task ReconcilesCleanWithTheCollectionHealthValidator()
    {
        var seed = BuildSeed();
        var cassFeeds = seed.SourceFeeds.Where(f => f.CompanyId == CassId).ToArray();

        // The collection context a filtered pass produces: only the retained company's feeds reach the
        // collectors, because only that company was seeded into the repository.
        var filteredContext = new CollectionContext([], cassFeeds);

        var filteredValidator = new SeedFeedInventoryValidator(
            Filtered(seed, "CASS"), NullLogger<SeedFeedInventoryValidator>.Instance);

        var report = await filteredValidator.ValidateAsync(filteredContext, CancellationToken.None);

        Assert.False(report.HasWarnings);
        Assert.Empty(report.Warnings);

        // Negative control: reading the WHOLE seed against the same filtered context DOES warn — which is
        // exactly why the decorator has to sit in front of the validator too, not only the seeder.
        var unfilteredValidator = new SeedFeedInventoryValidator(
            new StubSeedSource(seed), NullLogger<SeedFeedInventoryValidator>.Instance);

        Assert.True(
            (await unfilteredValidator.ValidateAsync(filteredContext, CancellationToken.None)).HasWarnings);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Filtered(BuildSeed(), "CASS").GetSeedAsync(cts.Token));
    }

    private sealed class StubSeedSource(CompanySeedData seed) : ICompanySeedSource
    {
        public Task<CompanySeedData> GetSeedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(seed);
        }
    }
}
