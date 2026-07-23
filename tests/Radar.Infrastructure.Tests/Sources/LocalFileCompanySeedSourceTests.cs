using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.Collectors;
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

        // No sourceFeeds/themes in this file: feeds empty, themes empty per company.
        Assert.Empty(seed.SourceFeeds);
        Assert.All(seed.Companies, c => Assert.Empty(c.Themes));
    }

    private const string FeedsAndThemesJson = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Acme Corp",
              "ticker": "ACME",
              "themes": [ "space", "  defence  ", "", "   " ],
              "sourceFeeds": [
                { "type": "rss", "name": "Acme Investor News", "url": "https://example.com/acme.rss" },
                { "type": "rss", "name": "No Url Feed" }
              ]
            },
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "Globex",
              "ticker": "GLBX"
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_ParsesSourceFeedsAndThemes_SkippingUrllessFeeds()
    {
        var path = WriteSeedFile(FeedsAndThemesJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        // Only the one valid feed is yielded; the url-less feed is skipped (never fabricated).
        var feed = Assert.Single(seed.SourceFeeds);
        Assert.Equal(AcmeId, feed.CompanyId);
        Assert.Equal("https://example.com/acme.rss", feed.Url);
        Assert.Equal("Acme Investor News", feed.Name);
        Assert.Equal("rss", feed.FeedType);
        Assert.Equal(FixedNow, feed.CreatedAtUtc);

        var acme = seed.Companies.Single(c => c.Id == AcmeId);
        Assert.Equal(new[] { "space", "defence" }, acme.Themes.ToArray());

        // Company with no sourceFeeds/themes yields no feeds and empty themes.
        var globex = seed.Companies.Single(c => c.Id == GlobexId);
        Assert.Empty(globex.Themes);
        Assert.DoesNotContain(seed.SourceFeeds, f => f.CompanyId == GlobexId);
    }

    [Fact]
    public async Task GetSeedAsync_DerivesDeterministicFeedIds()
    {
        var path = WriteSeedFile(FeedsAndThemesJson);

        var first = await CreateSource(path).GetSeedAsync(CancellationToken.None);
        var second = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(first.SourceFeeds.Count, second.SourceFeeds.Count);

        foreach (var feed in first.SourceFeeds)
        {
            var match = second.SourceFeeds.Single(
                f => f.CompanyId == feed.CompanyId && f.Url == feed.Url);
            Assert.Equal(feed.Id, match.Id);
        }
    }

    private const string WhitespacePaddedUrlJson = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Acme Corp",
              "ticker": "ACME",
              "sourceFeeds": [
                { "type": "rss", "name": "Padded", "url": "  https://example.com/acme.rss  " },
                { "type": "rss", "name": "Clean", "url": "https://example.com/acme.rss" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_FeedUrlWhitespace_DoesNotAffectStoredUrlOrId()
    {
        var path = WriteSeedFile(WhitespacePaddedUrlJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        // The padded and clean feeds normalize to the same trimmed Url and therefore the same
        // deterministic Id (the whitespace never leaks into the seed via the "type|url" composite).
        Assert.Equal(2, seed.SourceFeeds.Count);
        Assert.All(seed.SourceFeeds, f => Assert.Equal("https://example.com/acme.rss", f.Url));
        Assert.Single(seed.SourceFeeds.Select(f => f.Id).Distinct());
    }

    private const string SameUrlDifferentTypeSecJson = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Acme Corp",
              "ticker": "ACME",
              "sourceFeeds": [
                { "type": "sec", "name": "Acme SEC Filings", "url": "https://data.sec.gov/submissions/CIK0000123456.json" },
                { "type": "secform4", "name": "Acme Insider Form 4", "url": "https://data.sec.gov/submissions/CIK0000123456.json" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_SameUrlDifferentType_SecAndSecForm4_YieldsTwoDistinctFeeds()
    {
        var path = WriteSeedFile(SameUrlDifferentTypeSecJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        // Two feeds sharing one URL but differing by type no longer collide on Id.
        Assert.Equal(2, seed.SourceFeeds.Count);
        Assert.Equal(2, seed.SourceFeeds.Select(f => f.Id).Distinct().Count());

        // In the collection context they no longer collapse: each type surfaces exactly its feed.
        var context = new CollectionContext(seed.Companies, seed.SourceFeeds);
        var secFeed = Assert.Single(context.FeedsOfType("sec"));
        Assert.Equal("sec", secFeed.FeedType);
        var form4Feed = Assert.Single(context.FeedsOfType("secform4"));
        Assert.Equal("secform4", form4Feed.FeedType);
        Assert.NotEqual(secFeed.Id, form4Feed.Id);
    }

    private const string SameUrlThreeSecTypesJson = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Acme Corp",
              "ticker": "ACME",
              "sourceFeeds": [
                { "type": "sec", "name": "Acme SEC Filings", "url": "https://data.sec.gov/submissions/CIK0000123456.json" },
                { "type": "secform4", "name": "Acme Insider Form 4", "url": "https://data.sec.gov/submissions/CIK0000123456.json" },
                { "type": "sec13dg", "name": "Acme 13D/13G", "url": "https://data.sec.gov/submissions/CIK0000123456.json" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_SameUrlThreeSecTypes_YieldsThreeDistinctFeeds()
    {
        // Per spec 97 the feed-Id folds the feed type, so sec + secform4 + sec13dg sharing one submissions URL
        // no longer collide — each surfaces its own feed with a distinct Id (spec 100 adds the third).
        var path = WriteSeedFile(SameUrlThreeSecTypesJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(3, seed.SourceFeeds.Count);
        Assert.Equal(3, seed.SourceFeeds.Select(f => f.Id).Distinct().Count());

        var context = new CollectionContext(seed.Companies, seed.SourceFeeds);
        var secFeed = Assert.Single(context.FeedsOfType("sec"));
        var form4Feed = Assert.Single(context.FeedsOfType("secform4"));
        var ownershipFeed = Assert.Single(context.FeedsOfType("sec13dg"));
        Assert.Equal("sec13dg", ownershipFeed.FeedType);
        Assert.Equal(3, new[] { secFeed.Id, form4Feed.Id, ownershipFeed.Id }.Distinct().Count());
    }

    private const string SameUrlDifferentTypeNewsJson = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Acme Corp",
              "ticker": "ACME",
              "sourceFeeds": [
                { "type": "news", "name": "Acme GDELT", "url": "query=Acme%20Corp&ticker=ACME" },
                { "type": "newssearch", "name": "Acme Google News", "url": "query=Acme%20Corp&ticker=ACME" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_SameUrlDifferentType_NewsAndNewsSearch_YieldsTwoDistinctFeeds()
    {
        var path = WriteSeedFile(SameUrlDifferentTypeNewsJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(2, seed.SourceFeeds.Count);
        Assert.Equal(2, seed.SourceFeeds.Select(f => f.Id).Distinct().Count());

        var context = new CollectionContext(seed.Companies, seed.SourceFeeds);
        var newsFeed = Assert.Single(context.FeedsOfType("news"));
        Assert.Equal("news", newsFeed.FeedType);
        var newsSearchFeed = Assert.Single(context.FeedsOfType("newssearch"));
        Assert.Equal("newssearch", newsSearchFeed.FeedType);
        Assert.NotEqual(newsFeed.Id, newsSearchFeed.Id);
    }

    // The four spec-103 hiringats seed rows exactly as data/companies.json declares them (real company
    // ids + hand-verified board tokens from the 2026-07-06 reachability spike), each alongside another
    // feed so the spec-97 type|url feed-Id composite is exercised per company.
    private const string HiringAtsSeedJson = """
        {
          "companies": [
            {
              "id": "885ea986-041f-4fc2-8163-b815ae930a78",
              "name": "Mercury Systems, Inc.",
              "ticker": "MRCY",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Mercury Systems — News attention (Google News)", "url": "query=Mercury Systems&ticker=MRCY" },
                { "type": "hiringats", "name": "Mercury Systems — Open roles (Greenhouse ATS)", "url": "platform=greenhouse&board=mercury" }
              ]
            },
            {
              "id": "c29674f6-1409-4d91-8451-a5674fdb9f5c",
              "name": "Commvault Systems, Inc.",
              "ticker": "CVLT",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Commvault Systems — News attention (Google News)", "url": "query=Commvault&ticker=CVLT" },
                { "type": "hiringats", "name": "Commvault Systems — Open roles (Greenhouse ATS)", "url": "platform=greenhouse&board=commvault" }
              ]
            },
            {
              "id": "f0d50897-7161-40e6-a367-4ce63fc5aa8c",
              "name": "Agilysys, Inc.",
              "ticker": "AGYS",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Agilysys — News attention (Google News)", "url": "query=Agilysys&ticker=AGYS" },
                { "type": "hiringats", "name": "Agilysys — Open roles (Greenhouse ATS)", "url": "platform=greenhouse&board=agilysys" }
              ]
            },
            {
              "id": "a825bf45-a23f-431c-b392-a04a029f2400",
              "name": "Energy Recovery, Inc.",
              "ticker": "ERII",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Energy Recovery — News attention (Google News)", "url": "query=Energy Recovery&ticker=ERII" },
                { "type": "hiringats", "name": "Energy Recovery — Open roles (Lever ATS)", "url": "platform=lever&board=energyrecovery" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_HiringAtsFeeds_ParseWithExactTokensAndDistinctIds()
    {
        // Spec 103: the four verified companies each carry one hiringats feed with the exact
        // platform=…&board=… token; every feed Id is distinct (spec 97 folds the feed TYPE into the Id,
        // so a hiringats feed can never collide with the same company's other feeds).
        var path = WriteSeedFile(HiringAtsSeedJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        var context = new CollectionContext(seed.Companies, seed.SourceFeeds);
        var hiringFeeds = context.FeedsOfType("hiringats");
        Assert.Equal(4, hiringFeeds.Count);

        var byCompany = hiringFeeds.ToDictionary(f => f.CompanyId, f => f.Url);
        Assert.Equal(
            "platform=greenhouse&board=mercury",
            byCompany[Guid.Parse("885ea986-041f-4fc2-8163-b815ae930a78")]);
        Assert.Equal(
            "platform=greenhouse&board=commvault",
            byCompany[Guid.Parse("c29674f6-1409-4d91-8451-a5674fdb9f5c")]);
        Assert.Equal(
            "platform=greenhouse&board=agilysys",
            byCompany[Guid.Parse("f0d50897-7161-40e6-a367-4ce63fc5aa8c")]);
        Assert.Equal(
            "platform=lever&board=energyrecovery",
            byCompany[Guid.Parse("a825bf45-a23f-431c-b392-a04a029f2400")]);

        // Every feed Id in the whole seed (hiringats + the sibling newssearch feeds) is distinct.
        Assert.Equal(
            seed.SourceFeeds.Count,
            seed.SourceFeeds.Select(f => f.Id).Distinct().Count());
    }

    // The three spec-127 patents seed rows exactly as data/companies.json declares them (real company ids +
    // verified assignee organization names), each alongside another feed so the spec-97 type|url feed-Id
    // composite is exercised per company. RKLB is absent from the 43-company universe, so only 3 are seeded
    // (partial coverage is normal, like usaspending 3/43 and hiringats 4/43).
    private const string PatentsSeedJson = """
        {
          "companies": [
            {
              "id": "885ea986-041f-4fc2-8163-b815ae930a78",
              "name": "Mercury Systems, Inc.",
              "ticker": "MRCY",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Mercury Systems — News attention (Google News)", "url": "query=Mercury Systems&ticker=MRCY" },
                { "type": "patents", "name": "Mercury Systems — Recent granted patents (PatentsView)", "url": "assignee=Mercury Systems, Inc." }
              ]
            },
            {
              "id": "a825bf45-a23f-431c-b392-a04a029f2400",
              "name": "Energy Recovery, Inc.",
              "ticker": "ERII",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Energy Recovery — News attention (Google News)", "url": "query=Energy Recovery&ticker=ERII" },
                { "type": "patents", "name": "Energy Recovery — Recent granted patents (PatentsView)", "url": "assignee=Energy Recovery, Inc." }
              ]
            },
            {
              "id": "23ddc629-d6d2-4877-9ea8-aa597de3606e",
              "name": "Eos Energy Enterprises, Inc.",
              "ticker": "EOSE",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Eos Energy Enterprises — News attention (Google News)", "url": "query=Eos Energy Enterprises&ticker=EOSE" },
                { "type": "patents", "name": "Eos Energy Enterprises — Recent granted patents (PatentsView)", "url": "assignee=Eos Energy Enterprises, Inc." }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_PatentsFeeds_ParseWithExactTokensAndDistinctIds()
    {
        // Spec 127: the three verified companies each carry one patents feed with the exact assignee=… token;
        // every feed Id is distinct (spec 97 folds the feed TYPE into the Id, so a patents feed can never
        // collide with the same company's other feeds).
        var path = WriteSeedFile(PatentsSeedJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        var context = new CollectionContext(seed.Companies, seed.SourceFeeds);
        var patentFeeds = context.FeedsOfType("patents");
        Assert.Equal(3, patentFeeds.Count);

        var byCompany = patentFeeds.ToDictionary(f => f.CompanyId, f => f.Url);
        Assert.Equal(
            "assignee=Mercury Systems, Inc.",
            byCompany[Guid.Parse("885ea986-041f-4fc2-8163-b815ae930a78")]);
        Assert.Equal(
            "assignee=Energy Recovery, Inc.",
            byCompany[Guid.Parse("a825bf45-a23f-431c-b392-a04a029f2400")]);
        Assert.Equal(
            "assignee=Eos Energy Enterprises, Inc.",
            byCompany[Guid.Parse("23ddc629-d6d2-4877-9ea8-aa597de3606e")]);

        // Every feed Id in the whole seed (patents + the sibling newssearch feeds) is distinct.
        Assert.Equal(
            seed.SourceFeeds.Count,
            seed.SourceFeeds.Select(f => f.Id).Distinct().Count());
    }

    // The three spec-128 fccauth seed rows exactly as data/companies.json declares them (real company ids +
    // verified grantee organization names), each alongside another feed so the spec-97 type|url feed-Id
    // composite is exercised per company. RKLB is absent from the universe, so only 3 are seeded (partial
    // coverage is normal, like usaspending 3/43 and patents 3/43).
    private const string FccSeedJson = """
        {
          "companies": [
            {
              "id": "885ea986-041f-4fc2-8163-b815ae930a78",
              "name": "Mercury Systems, Inc.",
              "ticker": "MRCY",
              "sourceFeeds": [
                { "type": "patents", "name": "Mercury Systems — Recent granted patents (PatentsView)", "url": "assignee=Mercury Systems, Inc." },
                { "type": "fccauth", "name": "Mercury Systems — Recent FCC equipment authorizations (EAS)", "url": "grantee=Mercury Systems, Inc." }
              ]
            },
            {
              "id": "a825bf45-a23f-431c-b392-a04a029f2400",
              "name": "Energy Recovery, Inc.",
              "ticker": "ERII",
              "sourceFeeds": [
                { "type": "patents", "name": "Energy Recovery — Recent granted patents (PatentsView)", "url": "assignee=Energy Recovery, Inc." },
                { "type": "fccauth", "name": "Energy Recovery — Recent FCC equipment authorizations (EAS)", "url": "grantee=Energy Recovery, Inc." }
              ]
            },
            {
              "id": "e8cffb0c-29b9-4db4-9eb3-c5e68fe72ba2",
              "name": "Bel Fuse Inc.",
              "ticker": "BELFB",
              "sourceFeeds": [
                { "type": "newssearch", "name": "Bel Fuse Inc. — News attention (Google News)", "url": "query=Bel Fuse&ticker=BELFB" },
                { "type": "fccauth", "name": "Bel Fuse Inc. — Recent FCC equipment authorizations (EAS)", "url": "grantee=Bel Fuse Inc." }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_FccFeeds_ParseWithExactTokensAndDistinctIds()
    {
        // Spec 128: the three verified companies each carry one fccauth feed with the exact grantee=… token;
        // every feed Id is distinct (spec 97 folds the feed TYPE into the Id, so an fccauth feed can never
        // collide with the same company's other feeds — incl. the same company's patents feed).
        var path = WriteSeedFile(FccSeedJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        var context = new CollectionContext(seed.Companies, seed.SourceFeeds);
        var fccFeeds = context.FeedsOfType("fccauth");
        Assert.Equal(3, fccFeeds.Count);

        var byCompany = fccFeeds.ToDictionary(f => f.CompanyId, f => f.Url);
        Assert.Equal(
            "grantee=Mercury Systems, Inc.",
            byCompany[Guid.Parse("885ea986-041f-4fc2-8163-b815ae930a78")]);
        Assert.Equal(
            "grantee=Energy Recovery, Inc.",
            byCompany[Guid.Parse("a825bf45-a23f-431c-b392-a04a029f2400")]);
        Assert.Equal(
            "grantee=Bel Fuse Inc.",
            byCompany[Guid.Parse("e8cffb0c-29b9-4db4-9eb3-c5e68fe72ba2")]);

        // Every feed Id in the whole seed (fccauth + the sibling patents/newssearch feeds) is distinct.
        Assert.Equal(
            seed.SourceFeeds.Count,
            seed.SourceFeeds.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public async Task GetSeedAsync_SameUrlDifferentType_FeedIdsStableAcrossCalls()
    {
        var path = WriteSeedFile(SameUrlDifferentTypeSecJson);

        var first = await CreateSource(path).GetSeedAsync(CancellationToken.None);
        var second = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(first.SourceFeeds.Count, second.SourceFeeds.Count);

        foreach (var feed in first.SourceFeeds)
        {
            var match = second.SourceFeeds.Single(
                f => f.CompanyId == feed.CompanyId && f.FeedType == feed.FeedType);
            Assert.Equal(feed.Id, match.Id);
        }
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

    // ---- Spec 117: curated followingTier parsing ----

    private const string FollowingTierJson = """
        {
          "companies": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Mega Corp",
              "ticker": "MEGA",
              "followingTier": "mega"
            },
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "name": "Untiered Corp",
              "ticker": "UNTD"
            },
            {
              "id": "33333333-3333-3333-3333-333333333333",
              "name": "Garbage Tier Corp",
              "ticker": "GRBG",
              "followingTier": "gigantic"
            },
            {
              "id": "44444444-4444-4444-4444-444444444444",
              "name": "Shouty Mid Corp",
              "ticker": "SHMD",
              "followingTier": "MID"
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSeedAsync_ParsesFollowingTier_CaseInsensitive_DefaultingToSmall()
    {
        // Spec 117: "mega" parses (lowercase seed value), the parse is case-insensitive ("MID"), an ABSENT
        // tier silently defaults to Small, and an unrecognized value ALSO defaults to Small without
        // throwing (the warning path — never hallucinate a tier). AD-14: the tier is curated metadata.
        var path = WriteSeedFile(FollowingTierJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(4, seed.Companies.Count);
        Assert.Equal(FollowingTier.Mega, seed.Companies.Single(c => c.Ticker == "MEGA").FollowingTier);
        Assert.Equal(FollowingTier.Small, seed.Companies.Single(c => c.Ticker == "UNTD").FollowingTier);
        Assert.Equal(FollowingTier.Small, seed.Companies.Single(c => c.Ticker == "GRBG").FollowingTier);
        Assert.Equal(FollowingTier.Mid, seed.Companies.Single(c => c.Ticker == "SHMD").FollowingTier);
    }

    [Fact]
    public async Task GetSeedAsync_DigitOnlyFollowingTier_DefaultsToSmall()
    {
        // A digit-only value like "1" would be coerced by Enum.TryParse to the tier at that ordinal
        // (Mid), which is garbage for this curated name field. It must be rejected like any other
        // unrecognized value and default to Small (mirrors CollectedEvidenceMapper.ParseQuality).
        const string json = """
            {
              "companies": [
                {
                  "id": "55555555-5555-5555-5555-555555555555",
                  "name": "Digit Tier Corp",
                  "ticker": "DGIT",
                  "followingTier": "1"
                }
              ]
            }
            """;
        var path = WriteSeedFile(json);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Equal(FollowingTier.Small, seed.Companies.Single(c => c.Ticker == "DGIT").FollowingTier);
    }

    [Fact]
    public async Task GetSeedAsync_NoFollowingTierInFile_AllCompaniesDefaultToSmall()
    {
        // The pre-117 seed shape (no followingTier anywhere) keeps working: every company is Small.
        var path = WriteSeedFile(TwoCompanyJson);

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.All(seed.Companies, c => Assert.Equal(FollowingTier.Small, c.FollowingTier));
    }

    [Fact]
    public async Task GetSeedAsync_MissingFile_ReturnsEmptyAndDoesNotThrow()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.json");

        var seed = await CreateSource(missing).GetSeedAsync(CancellationToken.None);

        Assert.Empty(seed.Companies);
        Assert.Empty(seed.Aliases);
        Assert.Empty(seed.SourceFeeds);
    }

    [Fact]
    public async Task GetSeedAsync_MalformedJson_ReturnsEmptyAndDoesNotThrow()
    {
        var path = WriteSeedFile("{ this is not valid json ");

        var seed = await CreateSource(path).GetSeedAsync(CancellationToken.None);

        Assert.Empty(seed.Companies);
        Assert.Empty(seed.Aliases);
        Assert.Empty(seed.SourceFeeds);
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
