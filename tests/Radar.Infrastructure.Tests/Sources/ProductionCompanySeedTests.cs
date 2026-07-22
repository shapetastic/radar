using Microsoft.Extensions.Logging.Abstractions;
using Radar.Application.EntityResolution;
using Radar.Infrastructure.Sources;

namespace Radar.Infrastructure.Tests.Sources;

/// <summary>
/// Guardrails over the SHIPPED watch universe (<c>data/companies.json</c>) — the curated, diversified efficacy
/// sample. These assertions pin two things a well-meaning later edit could silently undo: the universe size
/// (spec 125 expanded it 29 -> 43) and the ticker-collision rule for the four tickers that are substrings of
/// common English words. The universe is NOT a scoring input, so nothing here touches the fingerprint.
/// </summary>
public sealed class ProductionCompanySeedTests
{
    /// <summary>Universe size after the spec-125 gap-sector batch (29 existing + 14 added).</summary>
    private const int ExpectedCompanyCount = 43;

    /// <summary>
    /// <c>NewsAttentionCollector.IsRelevant</c> matches the ticker with an unanchored, case-insensitive
    /// <c>Contains</c> on the headline. These tickers are substrings of common English words ("deal"/"idea",
    /// "shoot", "latex", "Shenzhen"), so their newssearch feed must carry NO <c>ticker=</c> token — relevance
    /// is driven by the query phrase alone (same treatment as V/Visa). False-positive media evidence inflates
    /// Attention, and radar-formula-v8 credits collapsed distinct-publisher breadth into the reach term, so
    /// junk headlines would distort the notedness discount the 117->124 calibration arc settled.
    /// </summary>
    private static readonly string[] TickersWithoutTickerToken = ["DEA", "SHOO", "ATEX", "SHEN"];

    [Fact]
    public async Task ProductionSeed_ContainsTheExpectedUniverseSize()
    {
        var seed = await LoadProductionSeedAsync();

        Assert.Equal(ExpectedCompanyCount, seed.Companies.Count);
        Assert.Equal(ExpectedCompanyCount, seed.Companies.Select(c => c.Id).Distinct().Count());
        Assert.Equal(
            ExpectedCompanyCount,
            seed.Companies.Select(c => c.Ticker ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Theory]
    [InlineData("DEA")]
    [InlineData("SHOO")]
    [InlineData("ATEX")]
    [InlineData("SHEN")]
    public async Task ProductionSeed_CollidingTickers_HaveNoTickerTokenInNewsSearchFeed(string ticker)
    {
        var url = await GetNewsSearchUrlAsync(ticker);

        Assert.DoesNotContain("ticker=", url, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("query=", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionSeed_DistinctiveTicker_KeepsTheTickerToken()
    {
        // Honesty control for the theory above: a distinctive ticker still carries the token.
        var url = await GetNewsSearchUrlAsync("HWKN");

        Assert.Contains("&ticker=HWKN", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionSeed_EveryCompanyWithoutACollidingTicker_KeepsTheTickerToken()
    {
        var seed = await LoadProductionSeedAsync();
        var exempt = new HashSet<string>(TickersWithoutTickerToken, StringComparer.OrdinalIgnoreCase)
        {
            // Visa's single-letter ticker matched almost any headline (spec 120 fix).
            "V",
        };

        foreach (var company in seed.Companies)
        {
            var feed = seed.SourceFeeds.SingleOrDefault(
                f => f.CompanyId == company.Id
                    && string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(feed);

            var ticker = company.Ticker ?? string.Empty;
            if (exempt.Contains(ticker))
            {
                continue;
            }

            Assert.Contains(
                $"&ticker={ticker}",
                feed!.Url,
                StringComparison.Ordinal);
        }
    }

    private static async Task<string> GetNewsSearchUrlAsync(string ticker)
    {
        var seed = await LoadProductionSeedAsync();
        var company = Assert.Single(
            seed.Companies,
            c => string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        var feed = Assert.Single(
            seed.SourceFeeds,
            f => f.CompanyId == company.Id
                && string.Equals(f.FeedType, "newssearch", StringComparison.OrdinalIgnoreCase));
        return feed.Url;
    }

    private static Task<CompanySeedData> LoadProductionSeedAsync()
    {
        var path = Path.Combine(LocateRepoRoot(), "data", "companies.json");
        Assert.True(File.Exists(path), $"Expected the production company seed at {path}.");

        var source = new LocalFileCompanySeedSource(
            new LocalFileCompanySeedOptions { FilePath = path },
            NullLogger<LocalFileCompanySeedSource>.Instance,
            TimeProvider.System);

        return source.GetSeedAsync(CancellationToken.None);
    }

    private static string LocateRepoRoot()
    {
        // Walk up from the test assembly's base directory to the repo root (the folder holding Radar.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Radar.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
